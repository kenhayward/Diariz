using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>Token counts from an OpenAI-compatible response's <c>usage</c> block. All nullable: plenty of
/// compatible servers omit some or all of it, and a missing count must not cost the caller its timing.</summary>
public readonly record struct LlmUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

/// <summary>Reads the <c>usage</c> block out of an OpenAI-compatible response body.</summary>
public static class LlmUsageParser
{
    /// <summary>Best-effort parse. Returns false rather than throwing on anything unexpected - this runs on a
    /// telemetry path, where turning "could not measure the call" into "the call failed" is far worse than a
    /// missing token count.</summary>
    public static bool TryParse(string? json, out LlmUsage usage)
    {
        usage = default;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return false;

            var prompt = ReadInt(u, "prompt_tokens");
            var completion = ReadInt(u, "completion_tokens");
            var total = ReadInt(u, "total_tokens")
                        // Servers that report the two halves but not the sum are common enough to be worth
                        // deriving, so the Performance view is not full of blank totals.
                        ?? (prompt is null && completion is null ? null : (prompt ?? 0) + (completion ?? 0));

            if (prompt is null && completion is null && total is null) return false;
            usage = new LlmUsage(prompt, completion, total);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}

/// <summary>One in-flight LLM call. Disposing finishes it.</summary>
public interface ILlmSpan : IDisposable
{
    void SetStatusCode(int statusCode);
    void SetUsage(LlmUsage usage);
}

/// <summary>Opens spans for outbound LLM calls. An interface (rather than calling <c>SentrySdk</c> directly)
/// so the handler can be unit-tested without initialising the SDK - see <c>FakeLlmTrace</c>.</summary>
public interface ILlmTrace
{
    ILlmSpan StartSpan(string op, string description);
}

/// <summary>Times every outbound LLM call and records its token usage.
///
/// Attached to the typed <see cref="HttpClient"/> registrations of the LLM clients, so one handler covers
/// all of them and any client added later is instrumented for free.
///
/// WHY THIS EXISTS: ASP.NET's Sentry integration instruments incoming HTTP requests and nothing else. Every
/// LLM call made from a BackgroundService - summaries, minutes, tags, actions, embeddings, formula runs -
/// therefore produced no timing at all. The endpoint that starts a formula run reported ~132 ms, which is
/// the enqueue; the model call it kicked off was invisible.
/// </summary>
public sealed class LlmTelemetryHandler : DelegatingHandler
{
    /// <summary>Matches the OpenTelemetry GenAI convention, so these group sensibly alongside future spans.</summary>
    public const string Op = "gen_ai.request";

    private readonly ILlmTrace _trace;

    public LlmTelemetryHandler(ILlmTrace trace) => _trace = trace;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Scheme/host/path only. A query string is exactly how the SignalR JWT reached a transaction name
        // once already; nothing in an LLM URL's query is worth carrying, so it is dropped outright rather
        // than scrubbed after the fact.
        var uri = request.RequestUri;
        var target = uri is null ? "(no uri)" : uri.GetLeftPart(UriPartial.Path);

        using var span = _trace.StartSpan(Op, $"{request.Method} {target}");

        var response = await base.SendAsync(request, ct);
        span.SetStatusCode((int)response.StatusCode);

        // `usage` only exists on a buffered JSON body. Streaming responses (SSE) are left strictly alone:
        // buffering one would defeat streaming entirely, leaving the chat UI silent until the model
        // finished instead of showing tokens as they arrive.
        if (IsJson(response) && LlmUsageParser.TryParse(await ReadForUsageAsync(response, ct), out var usage))
            span.SetUsage(usage);

        return response;
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is "application/json";

    /// <summary>Read the body so `usage` can be parsed out of it, WITHOUT costing the caller its own read.
    ///
    /// This relies on <see cref="HttpContent.ReadAsStringAsync(CancellationToken)"/> buffering: the content is
    /// loaded into an internal buffer on first read, and every later read - including the LLM client's own -
    /// is served from that buffer. So the caller still sees the full body.
    ///
    /// An earlier version copied the bytes into a replacement <c>ByteArrayContent</c> and swapped it onto the
    /// response, on the assumption that a consumed stream could not be re-read. Deleting that made no test
    /// fail even against single-consumption <c>StreamContent</c>, because the buffering above already covers
    /// it - so it was removed rather than kept as reassurance nobody had checked.
    ///
    /// This is exactly why streaming responses are excluded by the caller: buffering an SSE stream WOULD
    /// break it, by holding every token until the model finished.</summary>
    private static async Task<string?> ReadForUsageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            // Never let a telemetry read break the call it is measuring.
            return null;
        }
    }
}

/// <summary>Real <see cref="ILlmTrace"/>: opens a child span on whatever transaction is current.
///
/// Returns an inert span when there is no active transaction, which is the correct behaviour rather than an
/// error - a parentless span is dropped by the SDK anyway, and telemetry is entirely optional here.</summary>
public sealed class SentryLlmTrace : ILlmTrace
{
    public ILlmSpan StartSpan(string op, string description)
    {
        var parent = SentrySdk.GetSpan();
        return parent is null ? NullLlmSpan.Instance : new Span(parent.StartChild(op, description));
    }

    private sealed class Span : ILlmSpan
    {
        private readonly ISpan _span;
        public Span(ISpan span) => _span = span;

        public void SetStatusCode(int statusCode)
        {
            _span.SetExtra("http.status_code", statusCode);
            _span.Status = statusCode is >= 200 and < 400 ? SpanStatus.Ok : SpanStatus.UnknownError;
        }

        // Token counts only. Never the prompt or the completion: those are meeting content, and keeping
        // them out of telemetry is the entire point of SentryScrubber.
        public void SetUsage(LlmUsage usage)
        {
            if (usage.PromptTokens is { } p) _span.SetExtra("gen_ai.usage.input_tokens", p);
            if (usage.CompletionTokens is { } c) _span.SetExtra("gen_ai.usage.output_tokens", c);
            if (usage.TotalTokens is { } t) _span.SetExtra("gen_ai.usage.total_tokens", t);
        }

        public void Dispose() => _span.Finish();
    }
}

/// <summary>Does nothing. Used when there is no active transaction, and as the default in tests.</summary>
public sealed class NullLlmSpan : ILlmSpan
{
    public static readonly NullLlmSpan Instance = new();
    public void SetStatusCode(int statusCode) { }
    public void SetUsage(LlmUsage usage) { }
    public void Dispose() { }
}
