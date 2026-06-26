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
}
