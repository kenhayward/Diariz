using System.Text.Json;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Token counts from an OpenAI-compatible response's <c>usage</c> block. All nullable: plenty of
/// compatible servers omit some or all of it, and a missing count must not cost the caller its timing.</summary>
public readonly record struct LlmUsage(
    int? PromptTokens, int? CompletionTokens, int? TotalTokens, int? ReasoningTokens = null);

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

            // Reported by reasoning models under completion_tokens_details. Absent almost everywhere
            // else, and absent must stay null rather than becoming 0.
            int? reasoning = null;
            if (u.TryGetProperty("completion_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
                reasoning = ReadInt(details, "reasoning_tokens");

            if (prompt is null && completion is null && total is null) return false;
            usage = new LlmUsage(prompt, completion, total, reasoning);
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

/// <summary>Folds a bucketed token count into a span's description.
///
/// WHY THE DESCRIPTION, of all places: GlitchTip persists no span-level data. Its parquet schema is
/// <c>(organization_id, project_id, transaction_name, span_id, transaction_id, op, description, duration,
/// timestamp)</c> and nothing else, so anything set via <c>SetExtra</c> is transmitted by the SDK and
/// discarded on ingest. The description is the only free-text field that survives.
///
/// WHY BUCKETED: the span breakdown does <c>GROUP BY op, description</c>. Exact per-call counts would make
/// every span its own group - forty one-off rows instead of "chat/completions, 1.8s avg over 40 calls" -
/// destroying the aggregate view these numbers exist to inform. Six buckets keep grouping meaningful while
/// still showing which calls are large.</summary>
public static class LlmSpanDescription
{
    public static string WithUsage(string? description, LlmUsage usage)
    {
        var suffix = Bucket(usage.TotalTokens);
        if (suffix is null) return description ?? "";
        if (string.IsNullOrEmpty(description)) return suffix;
        // Idempotent: a description that already carries a bucket is left alone, so a retry cannot stack
        // suffixes onto the same span.
        return description.EndsWith(suffix, StringComparison.Ordinal) ? description : $"{description} {suffix}";
    }

    private static string? Bucket(int? total) => total switch
    {
        null => null,
        < 500 => "(<500 tokens)",
        < 1_000 => "(~500 tokens)",
        < 5_000 => "(~1k tokens)",
        < 10_000 => "(~5k tokens)",
        < 50_000 => "(~10k tokens)",
        _ => "(50k+ tokens)",
    };
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
    private readonly ILlmUsageSink _sink;

    public LlmTelemetryHandler(ILlmTrace trace, ILlmUsageSink sink)
    {
        _trace = trace;
        _sink = sink;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Scheme/host/path only. A query string is exactly how the SignalR JWT reached a transaction name
        // once already; nothing in an LLM URL's query is worth carrying, so it is dropped outright rather
        // than scrubbed after the fact.
        var uri = request.RequestUri;
        var target = uri is null ? "(no uri)" : uri.GetLeftPart(UriPartial.Path);

        using var span = _trace.StartSpan(Op, $"{request.Method} {target}");

        var scope = LlmCallScope.Active;
        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var promptChars = await PromptCharsAsync(request, ct);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            // A transport failure or a timeout is exactly the expensive case an administrator needs to
            // see, so it is recorded before the exception continues on to the caller unchanged.
            clock.Stop();
            Record(scope, target, request, startedAt, clock, promptChars, null, default, ErrorKindOf(ex));
            throw;
        }

        span.SetStatusCode((int)response.StatusCode);

        // `usage` only exists on a buffered JSON body. Streaming responses (SSE) are left strictly alone:
        // buffering one would defeat streaming entirely, leaving the chat UI silent until the model
        // finished instead of showing tokens as they arrive.
        var usage = default(LlmUsage);
        if (IsJson(response) && LlmUsageParser.TryParse(await ReadForUsageAsync(response, ct), out var parsed))
        {
            usage = parsed;
            span.SetUsage(parsed);
        }

        clock.Stop();
        var status = (int)response.StatusCode;
        Record(
            scope, target, request, startedAt, clock, promptChars, status, usage,
            response.IsSuccessStatusCode ? null : $"Http{status}");

        return response;
    }

    /// <summary>Length of the serialized request body, used as a proxy for prompt size so no call site has
    /// to report it. Best-effort: a body that cannot be read costs a null, never the call.</summary>
    private static async Task<int?> PromptCharsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return null;
        try
        {
            return (await request.Content.ReadAsStringAsync(ct)).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the <c>model</c> property out of the request body. Never throws - an unparseable or
    /// absent body costs an empty string, never the call.</summary>
    private static string ModelOf(HttpRequestMessage request)
    {
        try
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? string.Empty;
            return string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ErrorKindOf(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "Timeout",
        HttpRequestException => "Transport",
        _ => ex.GetType().Name,
    };

    /// <summary>Builds the row and hands it to the sink. Content is never included - only counts, sizes
    /// and identifiers.</summary>
    private void Record(
        LlmCallScope? scope, string target, HttpRequestMessage request, DateTimeOffset startedAt,
        System.Diagnostics.Stopwatch clock, int? promptChars, int? statusCode, LlmUsage usage,
        string? errorKind)
    {
        _sink.Record(new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = scope?.OperationId ?? Guid.NewGuid(),
            Sequence = scope?.NextSequence() ?? 1,
            Kind = scope?.Kind ?? LlmCallKind.Unknown,
            UserId = scope?.UserId,
            UserEmail = scope?.UserEmail ?? string.Empty,
            RecordingId = scope?.RecordingId,
            RecordingTitle = scope?.RecordingTitle,
            SectionId = scope?.SectionId,
            SectionName = scope?.SectionName,
            Model = ModelOf(request),
            Endpoint = target,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMilliseconds(clock.Elapsed.TotalMilliseconds),
            DurationMs = (int)clock.ElapsedMilliseconds,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            ReasoningTokens = usage.ReasoningTokens,
            TotalTokens = usage.TotalTokens,
            PromptChars = promptChars,
            Streamed = false,
            Success = errorKind is null,
            StatusCode = statusCode,
            ErrorKind = errorKind,
        });
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
            // The extras are kept even though GlitchTip drops every span-level attribute on ingest: they are
            // the correct, structured home for this, and a deployment pointed at real Sentry (or a future
            // GlitchTip that stores span data) gets exact counts for free. The description below is what
            // actually survives today.
            if (usage.PromptTokens is { } p) _span.SetExtra("gen_ai.usage.input_tokens", p);
            if (usage.CompletionTokens is { } c) _span.SetExtra("gen_ai.usage.output_tokens", c);
            if (usage.TotalTokens is { } t) _span.SetExtra("gen_ai.usage.total_tokens", t);

            _span.Description = LlmSpanDescription.WithUsage(_span.Description, usage);
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
