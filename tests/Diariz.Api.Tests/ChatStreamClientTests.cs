using System.Net;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class ChatStreamClientTests
{
    // ---- ParseStreamLine (pure) ----

    [Fact]
    public void ParseStreamLine_NonDataLine_ReturnsNull_NotDone()
    {
        var token = ChatStreamClient.ParseStreamLine(": keep-alive", out var done);
        Assert.Null(token);
        Assert.False(done);
    }

    [Fact]
    public void ParseStreamLine_Blank_ReturnsNull_NotDone()
    {
        Assert.Null(ChatStreamClient.ParseStreamLine("", out var done));
        Assert.False(done);
    }

    [Fact]
    public void ParseStreamLine_Done_SignalsDone()
    {
        var token = ChatStreamClient.ParseStreamLine("data: [DONE]", out var done);
        Assert.Null(token);
        Assert.True(done);
    }

    [Fact]
    public void ParseStreamLine_ExtractsDeltaContent()
    {
        var line = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}";
        var token = ChatStreamClient.ParseStreamLine(line, out var done);
        Assert.Equal("Hello", token);
        Assert.False(done);
    }

    [Fact]
    public void ParseStreamLine_RoleOnlyDelta_ReturnsNull()
    {
        // The first frame often carries only the role, no content.
        var line = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}";
        Assert.Null(ChatStreamClient.ParseStreamLine(line, out var done));
        Assert.False(done);
    }

    [Fact]
    public void ParseStreamLine_Malformed_ReturnsNull()
    {
        Assert.Null(ChatStreamClient.ParseStreamLine("data: {not json", out var done));
        Assert.False(done);
    }

    // ---- StreamAsync (over a fake SSE handler) ----

    [Fact]
    public async Task StreamAsync_YieldsContentDeltas_InOrder()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new FakeHttpMessageHandler(sse);
        var client = new ChatStreamClient(new HttpClient(handler));
        var cfg = new Diariz.Api.Services.SummarizationRequestConfig("http://llm.test/v1", "sk-x", "m", 60);

        var tokens = new List<string>();
        await foreach (var t in client.StreamAsync(cfg, [new ChatMessage("user", "hi")], default))
            tokens.Add(t);

        Assert.Equal(["Hel", "lo"], tokens);
        Assert.Equal("http://llm.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-x", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("\"stream\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task StreamAsync_HttpError_Throws()
    {
        var handler = new FakeHttpMessageHandler("nope", HttpStatusCode.InternalServerError);
        var client = new ChatStreamClient(new HttpClient(handler));
        var cfg = new Diariz.Api.Services.SummarizationRequestConfig("http://llm.test/v1", "", "m", 60);

        await Assert.ThrowsAsync<ChatStreamException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(cfg, [new ChatMessage("user", "hi")], default)) { }
        });
    }

    // ---- Inactivity timeout ----
    // The allowance is the gap BETWEEN chunks, not the total call. A slow local model streaming a long
    // answer legitimately runs for minutes; a total-duration cap would abort exactly the case the
    // configured timeout exists to support.

    private static SummarizationRequestConfig Config(int timeoutSeconds) =>
        new("http://llm.test/v1", "sk-x", "m", timeoutSeconds);

    [Fact]
    public async Task StreamChunks_Throws_WhenTheUpstreamGoesSilent()
    {
        var script = new[]
        {
            (TimeSpan.Zero, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}"),
            (TimeSpan.FromSeconds(30), "data: [DONE]"), // never arrives within the 1s allowance
        };
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });

        var ex = await Assert.ThrowsAsync<ChatStreamException>(async () =>
        {
            await foreach (var _ in client.StreamChunksAsync(Config(1), [], null)) { }
        });

        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public async Task StreamChunks_Completes_WhenChunksAreSlowButKeepArriving()
    {
        // Six 200ms gaps = 1.2s total, longer than the 1s timeout, but no single gap reaches it.
        // This is the test that fails if the idle timer is ever "simplified" into a total-duration cap.
        var script = Enumerable.Range(0, 5)
            .Select(i => (TimeSpan.FromMilliseconds(200),
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{i}\"}}}}]}}"))
            .Append((TimeSpan.FromMilliseconds(200), "data: [DONE]"))
            .ToArray();
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });

        var seen = 0;
        await foreach (var _ in client.StreamChunksAsync(Config(1), [], null)) seen++;

        Assert.Equal(5, seen);
    }

    [Fact]
    public async Task StreamChunks_StaysQuiet_WhenTheCallerCancels()
    {
        // A user pressing Stop must not be reported as a timeout: ChatController catches
        // OperationCanceledException silently, and turning it into a ChatStreamException would show
        // the user a spurious error for their own action.
        var script = new[] { (TimeSpan.FromSeconds(30), "data: [DONE]") };
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamChunksAsync(Config(60), [], null, cts.Token)) { }
        });
    }
}
