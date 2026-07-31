using System.Net;
using System.Net.Http.Json;
using System.Text;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

/// <summary>
/// The LLM call is the slowest and most failure-prone thing the API does, and until this it was
/// invisible: ASP.NET's Sentry integration instruments incoming requests only, so anything a
/// BackgroundService did - every summary, every formula run - produced no timing at all.
/// </summary>
public class LlmUsageParserTests
{
    [Fact]
    public void TryParse_ReadsTheUsageBlock()
    {
        var json = """
        {"choices":[{"message":{"content":"hi"}}],
         "usage":{"prompt_tokens":120,"completion_tokens":34,"total_tokens":154}}
        """;

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Equal(120, usage.PromptTokens);
        Assert.Equal(34, usage.CompletionTokens);
        Assert.Equal(154, usage.TotalTokens);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenThereIsNoUsageBlock()
    {
        // Plenty of OpenAI-compatible servers omit `usage` entirely. That is not an error, and it
        // must not cost the caller its span - the timing is useful even with no token counts.
        Assert.False(LlmUsageParser.TryParse("""{"choices":[{"message":{"content":"hi"}}]}""", out _));
    }

    [Fact]
    public void TryParse_TotalFallsBackToTheSumWhenAbsent()
    {
        var json = """{"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Equal(15, usage.TotalTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"usage":"unexpectedly a string"}""")]
    [InlineData("""{"usage":{"prompt_tokens":"NaN"}}""")]
    public void TryParse_NeverThrows_OnUnexpectedShapes(string json)
    {
        // This runs inside a telemetry path. A throw here would turn "we could not measure the call"
        // into "the call failed", which is a far worse outcome than a missing token count.
        var ex = Record.Exception(() => LlmUsageParser.TryParse(json, out _));
        Assert.Null(ex);
    }
}

public class LlmTelemetryHandlerTests
{
    private static HttpClient Client(FakeLlmTrace trace, FakeHttpMessageHandler inner) =>
        new(new LlmTelemetryHandler(trace) { InnerHandler = inner });

    private const string ChatJson =
        """{"choices":[{"message":{"content":"hi"}}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

    [Fact]
    public async Task StartsOneSpanPerCall_DescribedByMethodAndPath()
    {
        var trace = new FakeLlmTrace();
        var http = Client(trace, new FakeHttpMessageHandler(ChatJson));

        await http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { model = "m" }));

        var span = Assert.Single(trace.Spans);
        Assert.Equal("gen_ai.request", span.Op);
        Assert.Equal("POST https://llm.test/v1/chat/completions", span.Description);
        Assert.True(span.Finished);
    }

    [Fact]
    public async Task SpanDescriptionCarriesNoQueryString()
    {
        // A query string is exactly how the SignalR JWT leaked into transaction names once already.
        // Nothing about an LLM URL is worth that risk, so the query is dropped rather than scrubbed.
        var trace = new FakeLlmTrace();
        var http = Client(trace, new FakeHttpMessageHandler(ChatJson));

        await http.PostAsync("https://llm.test/v1/chat/completions?api-key=sk-secret", JsonContent.Create(new { }));

        Assert.DoesNotContain("sk-secret", Assert.Single(trace.Spans).Description);
    }

    [Fact]
    public async Task RecordsTokenUsageFromTheResponse()
    {
        var trace = new FakeLlmTrace();
        var http = Client(trace, new FakeHttpMessageHandler(ChatJson));

        await http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { }));

        var span = Assert.Single(trace.Spans);
        Assert.Equal(7, span.Usage?.PromptTokens);
        Assert.Equal(3, span.Usage?.CompletionTokens);
        Assert.Equal(10, span.Usage?.TotalTokens);
    }

    [Fact]
    public async Task LeavesTheResponseBodyReadableByTheCaller()
    {
        // The handler has to read the body to find `usage`, and a real network response can only be
        // read ONCE. If it does not put the content back, every LLM client in the app silently gets
        // an empty response - a far bigger failure than the telemetry it was added for.
        //
        // This deliberately does NOT use FakeHttpMessageHandler: its StringContent is an in-memory
        // buffer that can be re-read at will, so the test passed with the fix removed. A single-read
        // stream is what actually reproduces the hazard.
        var trace = new FakeLlmTrace();
        var http = new HttpClient(new LlmTelemetryHandler(trace) { InnerHandler = new OneShotStreamHandler(ChatJson) });

        var resp = await http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { }));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(ChatJson, body);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Serves the body as <see cref="StreamContent"/>, which - like a real network response, and
    /// unlike the in-memory <c>StringContent</c> the other fakes use - can only be consumed once. That is
    /// what makes this test able to fail if the handler stops replacing the content it buffers.</summary>
    private sealed class OneShotStreamHandler : HttpMessageHandler
    {
        private readonly string _body;
        public OneShotStreamHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(_body)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task DoesNotBufferAStreamingResponse()
    {
        // Buffering an SSE stream would defeat streaming entirely: the chat UI would sit silent
        // until the model finished instead of showing tokens as they arrive.
        var trace = new FakeLlmTrace();
        var inner = new FakeHttpMessageHandler("data: {}\n\n", HttpStatusCode.OK, "text/event-stream");
        var http = Client(trace, inner);

        var resp = await http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { }));

        Assert.Null(Assert.Single(trace.Spans).Usage);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FinishesTheSpanEvenWhenTheCallFails()
    {
        var trace = new FakeLlmTrace();
        var http = Client(trace, new FakeHttpMessageHandler("boom", HttpStatusCode.InternalServerError));

        await http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { }));

        var span = Assert.Single(trace.Spans);
        Assert.True(span.Finished);
        Assert.Equal(500, span.StatusCode);
    }

    [Fact]
    public async Task FinishesTheSpanWhenTheTransportThrows()
    {
        var trace = new FakeLlmTrace();
        var http = new HttpClient(new LlmTelemetryHandler(trace) { InnerHandler = new ThrowingHandler() });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync("https://llm.test/v1/chat/completions", JsonContent.Create(new { })));

        Assert.True(Assert.Single(trace.Spans).Finished);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
