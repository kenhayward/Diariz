using System.Net;
using System.Net.Http.Json;
using System.Text;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

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

    [Fact]
    public void TryParse_ReadsReasoningTokens_FromCompletionTokenDetails()
    {
        var json = """
        {"usage":{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,
                  "completion_tokens_details":{"reasoning_tokens":40}}}
        """;

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Equal(40, usage.ReasoningTokens);
    }

    [Fact]
    public void TryParse_LeavesReasoningNull_WhenTheServerDoesNotReportIt()
    {
        // Most local endpoints report no details block at all. Null must stay null: a reasoning count
        // of 0 would be a claim the model did no reasoning, which is not what silence means.
        var json = """{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Null(usage.ReasoningTokens);
    }

    [Fact]
    public void TryParse_NeverThrows_WhenDetailsIsTheWrongShape()
    {
        var json = """{"usage":{"prompt_tokens":10,"completion_tokens_details":"nonsense"}}""";
        var ex = Record.Exception(() => LlmUsageParser.TryParse(json, out _));
        Assert.Null(ex);
    }
}

public class LlmTelemetryHandlerTests
{
    private static HttpClient Client(FakeLlmTrace trace, FakeHttpMessageHandler inner) =>
        new(new LlmTelemetryHandler(trace, new FakeLlmUsageSink()) { InnerHandler = inner });

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
        var http = new HttpClient(new LlmTelemetryHandler(trace, new FakeLlmUsageSink()) { InnerHandler = new OneShotStreamHandler(ChatJson) });

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
        var http = new HttpClient(new LlmTelemetryHandler(trace, new FakeLlmUsageSink()) { InnerHandler = new ThrowingHandler() });

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

/// <summary>
/// Token counts ride in the span DESCRIPTION because GlitchTip persists no span-level data: its parquet
/// schema is (organization_id, project_id, transaction_name, span_id, transaction_id, op, description,
/// duration, timestamp) and nothing else, so SetExtra values are transmitted and discarded on ingest.
///
/// They are BUCKETED because the span breakdown does `GROUP BY op, description`. Exact per-call counts would
/// make every span its own group - forty one-off rows instead of "chat/completions, 1.8s avg over 40 calls" -
/// which would wreck the view the counts are meant to inform.
/// </summary>
public class LlmSpanDescriptionTests
{
    private const string Base = "POST https://llm.test/v1/chat/completions";

    [Theory]
    [InlineData(120, "(<500 tokens)")]
    [InlineData(499, "(<500 tokens)")]
    [InlineData(500, "(~500 tokens)")]
    [InlineData(999, "(~500 tokens)")]
    [InlineData(1000, "(~1k tokens)")]
    [InlineData(4999, "(~1k tokens)")]
    [InlineData(5000, "(~5k tokens)")]
    [InlineData(10000, "(~10k tokens)")]
    [InlineData(49999, "(~10k tokens)")]
    [InlineData(50000, "(50k+ tokens)")]
    [InlineData(1250000, "(50k+ tokens)")]
    public void BucketsTheTotal(int total, string expected)
    {
        var actual = LlmSpanDescription.WithUsage(Base, new LlmUsage(null, null, total));
        Assert.Equal($"{Base} {expected}", actual);
    }

    [Fact]
    public void UsesOnlySixDistinctSuffixes_SoGroupingStaysMeaningful()
    {
        // The whole point of bucketing. If this ever grows, the span breakdown starts fragmenting.
        var suffixes = Enumerable.Range(0, 2000)
            .Select(i => i * 97)
            .Select(t => LlmSpanDescription.WithUsage(Base, new LlmUsage(null, null, t)))
            .Distinct()
            .ToList();

        Assert.Equal(6, suffixes.Count);
    }

    [Fact]
    public void LeavesTheDescriptionAloneWhenThereIsNoTotal()
    {
        Assert.Equal(Base, LlmSpanDescription.WithUsage(Base, new LlmUsage(null, null, null)));
    }

    [Fact]
    public void IsIdempotent_SoARetriedCallDoesNotStackSuffixes()
    {
        var once = LlmSpanDescription.WithUsage(Base, new LlmUsage(10, 20, 30));
        var twice = LlmSpanDescription.WithUsage(once, new LlmUsage(10, 20, 30));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void HandlesANullDescription()
    {
        Assert.Equal("(<500 tokens)", LlmSpanDescription.WithUsage(null, new LlmUsage(1, 2, 3)));
    }
}

/// <summary>
/// The handler's second output: a durable row per call, attributed via the ambient <see cref="LlmCallScope"/>
/// and handed to <see cref="ILlmUsageSink"/>. This is additive to the Sentry span above, not a replacement -
/// those tests are untouched.
/// </summary>
public class LlmTelemetryHandlerUsageTests
{
    /// <summary>Returns a fixed, canned <see cref="HttpResponseMessage"/> regardless of the request - unlike
    /// <see cref="FakeHttpMessageHandler"/>, which builds the response from a body string, this takes the
    /// response object directly so a test can control status/content-type together.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_response);
    }

    private static HttpClient Client(LlmTelemetryHandler handler, HttpResponseMessage response)
    {
        handler.InnerHandler = new StubHandler(response);
        return new HttpClient(handler) { BaseAddress = new Uri("http://lmstudio:1234/") };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task RecordsTheCall_WithTheAmbientScopesAttribution()
    {
        var sink = new FakeLlmUsageSink();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        using var scope = LlmCallScope.Push(
            LlmCallKind.Summarize, userId, "owner@example.com", recordingId, "Standup");

        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            Json("""{"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}"""));
        await http.PostAsync("/v1/chat/completions", new StringContent("""{"model":"qwen"}"""));

        var call = Assert.Single(sink.Calls);
        Assert.Equal(LlmCallKind.Summarize, call.Kind);
        Assert.Equal(userId, call.UserId);
        Assert.Equal("owner@example.com", call.UserEmail);
        Assert.Equal(recordingId, call.RecordingId);
        Assert.Equal("Standup", call.RecordingTitle);
        Assert.Equal(scope.OperationId, call.OperationId);
        Assert.Equal(1, call.Sequence);
        Assert.Equal(100, call.PromptTokens);
        Assert.Equal(20, call.CompletionTokens);
        Assert.True(call.Success);
        Assert.Equal(200, call.StatusCode);
    }

    [Fact]
    public async Task RecordsAnUnattributedCall_AsUnknown_RatherThanDroppingIt()
    {
        // A client registered later without a scope must show up as a visible gap, not vanish. This is
        // the failure mode AddLlmClient exists to prevent, and silence would reintroduce it.
        var sink = new FakeLlmUsageSink();
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("""{"usage":{"total_tokens":5}}"""));

        await http.PostAsync("/v1/embeddings", new StringContent("{}"));

        Assert.Equal(LlmCallKind.Unknown, Assert.Single(sink.Calls).Kind);
    }

    [Fact]
    public async Task SequenceIncrementsAcrossCallsInOneScope()
    {
        var sink = new FakeLlmUsageSink();
        using var scope = LlmCallScope.Push(LlmCallKind.ChatMessage);

        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        Assert.Equal([1, 2], sink.Calls.Select(c => c.Sequence));
        Assert.Single(sink.Calls.Select(c => c.OperationId).Distinct());
    }

    [Fact]
    public async Task RecordsAFailedCall_WithItsStatusAndErrorKind()
    {
        // A log of only successful calls hides the expensive problem: the call that burned the whole
        // timeout and produced nothing.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Tags);
        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });

        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        var call = Assert.Single(sink.Calls);
        Assert.False(call.Success);
        Assert.Equal(500, call.StatusCode);
        Assert.Equal("Http500", call.ErrorKind);
    }

    /// <summary>Throws before any response is ever produced - models a connection refused / DNS failure /
    /// TLS handshake failure, i.e. <see cref="HttpRequestException"/> reaching the handler from
    /// <c>base.SendAsync</c> itself rather than from a completed (if unsuccessful) response.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task RecordsATransportFailure_AndStillLetsTheExceptionThrough()
    {
        // The try/catch around base.SendAsync is the most novel code this handler adds, and it is easy to
        // get subtly wrong: forget the Record call inside the catch, double-record on top of some other
        // path, or swallow the exception instead of rethrowing it. Every one of those regressions would
        // pass a suite that only ever exercises successful/HTTP-error responses - this is the one that
        // actually exercises base.SendAsync throwing.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Tags);
        var handler = new LlmTelemetryHandler(new FakeLlmTrace(), sink) { InnerHandler = new ThrowingHandler() };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://lmstudio:1234/") };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync("/v1/chat/completions", new StringContent("{}")));

        // Exactly one - not zero (the catch dropped it), not two (recorded twice on some double path).
        var call = Assert.Single(sink.Calls);
        Assert.False(call.Success);
        Assert.Equal("Transport", call.ErrorKind);
        Assert.Null(call.StatusCode);
    }

    [Fact]
    public async Task StoresNoPromptOrCompletionContent()
    {
        // The hard line: counts and sizes only. If this ever fails, meeting content is leaking into a
        // table an administrator browses.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            Json("""{"choices":[{"message":{"content":"the secret merger closes friday"}}],"usage":{"total_tokens":9}}"""));

        await http.PostAsync("/v1/chat/completions", new StringContent("""{"messages":[{"content":"confidential transcript"}]}"""));

        var serialized = System.Text.Json.JsonSerializer.Serialize(Assert.Single(sink.Calls));
        Assert.DoesNotContain("secret merger", serialized);
        Assert.DoesNotContain("confidential transcript", serialized);
    }

    [Fact]
    public async Task RecordsTheEndpointWithoutItsQueryString()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Embedding);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));

        await http.PostAsync("/v1/embeddings?api_key=supersecret", new StringContent("{}"));

        Assert.Equal("http://lmstudio:1234/v1/embeddings", Assert.Single(sink.Calls).Endpoint);
    }

    [Fact]
    // The real clients (ChatStreamClient/SummarizationClient/EmbeddingClient) all send JsonContent.Create,
    // whose Content-Type is exactly "application/json" - so the request here is built the same way, rather
    // than the default StringContent "text/plain", to exercise the actual JSON-detection path the handler
    // now gates its one body read on.
    public async Task RecordsPromptCharsAndModel_FromTheJsonRequestBody()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        var body = """{"model":"qwen2.5:14b","messages":[]}""";

        await http.PostAsync(
            "/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));

        var call = Assert.Single(sink.Calls);
        Assert.Equal(body.Length, call.PromptChars);
        Assert.Equal("qwen2.5:14b", call.Model);
    }

    [Fact]
    // Models DictationClient's multipart audio upload (up to 10 MiB per ChatController.Transcribe). The
    // stream throws if anything ever tries to read its bytes, proving the handler never buffers a non-JSON
    // request body - the bug this guards against forced the whole upload into memory and UTF-8-decoded the
    // binary audio, twice, on every dictation call. The stream still reports a Length (CanSeek = true), so
    // PromptChars can be recorded from the request's Content-Length header without any byte ever being read -
    // proving both halves of the fix in one test: sized without reading, and never buffered.
    public async Task RecordsPromptChars_FromContentLength_ForANonJsonRequest_WithoutReadingTheBody()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Dictation);

        using var audio = new ThrowsOnReadStream(4096);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(audio), "file", "clip.webm");
        var expectedLength = form.Headers.ContentLength;
        Assert.NotNull(expectedLength); // sanity: the fixture is set up to have a computable length

        using var req = new HttpRequestMessage(
            HttpMethod.Post, "http://lmstudio:1234/v1/audio/transcriptions") { Content = form };
        var handler = new LlmTelemetryHandler(new FakeLlmTrace(), sink)
        {
            InnerHandler = new StubHandler(Json("""{"text":"hello"}""")),
        };
        using var http = new HttpClient(handler);

        await http.SendAsync(req);

        var call = Assert.Single(sink.Calls);
        Assert.Equal((int)expectedLength!.Value, call.PromptChars);
        Assert.Equal(string.Empty, call.Model);
    }

    /// <summary>A stream that reports a computable <see cref="Length"/> (so Content-Length can be derived
    /// without touching the bytes) but throws if anything ever actually reads it - the strongest available
    /// proof that the telemetry handler never buffers a non-JSON request body.</summary>
    private sealed class ThrowsOnReadStream : Stream
    {
        private readonly long _length;
        public ThrowsOnReadStream(long length) => _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The telemetry handler read a non-JSON request body.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            throw new InvalidOperationException("The telemetry handler read a non-JSON request body.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new InvalidOperationException("The telemetry handler read a non-JSON request body.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage EventStream(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    [Fact]
    public async Task RecordsStreamedTrue_ForAnEventStreamResponse()
    {
        // A real streamed chat call was seen live with Streamed hardcoded false - a permanently-wrong
        // admin-visible column is worse than no column at all, because a filter on it gives a silently
        // wrong answer. This is visible for free from the content-type header; no body read required.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), EventStream("data: {}\n\n"));

        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        Assert.True(Assert.Single(sink.Calls).Streamed);
    }

    [Fact]
    public async Task RecordsStreamedFalse_ForAnOrdinaryJsonResponse()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));

        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        Assert.False(Assert.Single(sink.Calls).Streamed);
    }
}
