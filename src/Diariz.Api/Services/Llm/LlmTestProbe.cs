using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Services.Llm;

/// <summary>The result of one administrator-initiated test call, as shown in the model editor.
///
/// Unlike every other LLM result in the platform this one carries the model's <see cref="Response"/> text.
/// That is deliberate and bounded: it is returned in the HTTP response and never persisted - the usage log
/// still records counts only, so no meeting content or model output reaches storage.</summary>
public sealed record LlmTestOutcome(
    bool Ok,
    int? HttpStatus,
    /// <summary>Milliseconds to the first content token. Null when nothing ever arrived.</summary>
    int? TtftMs,
    int DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    int? TotalTokens,
    string? FinishReason,
    string? Response,
    /// <summary>The body that was actually sent, so "Copy as cURL" quotes the request that ran rather than
    /// one rebuilt from the editor's state afterwards. Never contains the API key.</summary>
    string RequestBodyJson,
    /// <summary>Matches <c>LlmCall.ErrorKind</c>'s vocabulary: Timeout, Transport, Http&lt;status&gt;.</summary>
    string? ErrorKind,
    string? Message,
    /// <summary>Which of the thirteen parameters the endpoint blamed, when it named one. Drives the
    /// editor's one-click "omit this here" fix.</summary>
    string? OffendingParameter,
    /// <summary>Which shape <see cref="Parsed"/> holds - Tags, Actions or Summary - or null when this
    /// group ran the built-in sample, or the call failed.</summary>
    string? ParsedKind = null,
    /// <summary>The reply run through the pipeline's OWN parser: an <c>IReadOnlyList&lt;ExtractedTag&gt;</c>,
    /// an <c>IReadOnlyList&lt;ExtractedAction&gt;</c>, or a <c>SummaryResult</c>, per
    /// <see cref="ParsedKind"/>. An EMPTY list is a real and important answer - it means the pipeline would
    /// have extracted nothing from this model's reply. Null when nothing was parsed (a sample-transcript
    /// group, or a failed call).
    ///
    /// <para>A real object, not a JSON string. It used to be the latter, and the string was built by a bare
    /// <c>JsonSerializer.Serialize</c> while ASP.NET camelCased the envelope around it - so the response
    /// said <c>parsedJson</c> on the outside and <c>Summary</c> on the inside, and the browser read
    /// nothing. Handing back the object lets one serializer name every property, which is the only way the
    /// two cannot disagree.</para>
    ///
    /// <para>Defaulted, so the probe's own construction sites are unchanged - it never parses. Parsing is
    /// the controller's job, because only it knows which call group ran.</para></summary>
    object? Parsed = null);

public interface ILlmTestProbe
{
    /// <summary>Runs one call with the given messages, streaming so time-to-first-token exists.</summary>
    Task<LlmTestOutcome> RunAsync(
        LlmRequestConfig config, IReadOnlyList<ChatMessage> messages, int maxResponseChars,
        CancellationToken ct = default);
}

/// <summary>Sends one sample call to a model so an administrator can see whether their endpoint and
/// parameters actually work, and what it costs when they do.
///
/// <b>The caller supplies the prompt.</b> Four call groups pass <see cref="LlmTestSample"/> - a fixed
/// miniature transcript that keeps their timings comparable between models. Tags, Actions and Summaries
/// pass the real prompt for a real recording, built by <c>LlmTestPromptFactory</c>, because for those the
/// question is not only "does this endpoint answer" but "is this model any good at the job".
///
/// <b>Streamed, always.</b> Time-to-first-token is the number the result leads with, and it does not exist
/// on a buffered response. It is also the number that distinguishes "the model is slow" from "the model
/// was still loading", which is the single most common thing an administrator is diagnosing here.
///
/// Failures are RETURNED, never thrown: a wrong endpoint is the main thing being tested for, and a 500
/// from our own API in response would tell the admin nothing about theirs.</summary>
public sealed class LlmTestProbe : ILlmTestProbe
{
    private readonly HttpClient _http;

    public LlmTestProbe(HttpClient http) => _http = http;

    public async Task<LlmTestOutcome> RunAsync(
        LlmRequestConfig config, IReadOnlyList<ChatMessage> messages, int maxResponseChars,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["stream"] = true,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
        };
        LlmRequestBody.Apply(body, config.Parameters);
        var requestJson = JsonSerializer.Serialize(body);

        var clock = Stopwatch.StartNew();
        TimeSpan? ttft = null;
        var text = new StringBuilder();
        var scanner = new SseUsageScanner();

        // The deadline is ours, not HttpClient's: every LLM client is registered with an infinite HttpClient
        // timeout so the resolved parameter is the single authority.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(body),
            };
            if (!string.IsNullOrEmpty(config.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            // ResponseHeadersRead, not the buffering default: without it the whole body is read before this
            // returns and every token measurement below collapses to the same number.
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var error = await SafeReadAsync(resp, deadline.Token);
                clock.Stop();
                var status = (int)resp.StatusCode;
                return Failed(
                    clock, requestJson, $"Http{status}", Truncate(error, 1000),
                    LlmErrorDiagnosis.OffendingParameter(error), status);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.Length == 0) continue;
                scanner.Feed(Encoding.UTF8.GetBytes(line + "\n"));

                var delta = ParseDelta(line, out var done);
                if (done) break;
                if (string.IsNullOrEmpty(delta)) continue;

                // First CONTENT, not first byte: a server that opens with keep-alive comments or an empty
                // role delta has not answered anything yet, and reporting that as the first token would
                // flatter a cold model into looking instant.
                ttft ??= clock.Elapsed;
                if (text.Length < maxResponseChars) text.Append(delta);
            }

            clock.Stop();
            var usage = scanner.Usage;
            return new LlmTestOutcome(
                Ok: true,
                HttpStatus: (int)resp.StatusCode,
                TtftMs: (int?)ttft?.TotalMilliseconds,
                DurationMs: (int)clock.ElapsedMilliseconds,
                PromptTokens: usage?.PromptTokens,
                CompletionTokens: usage?.CompletionTokens,
                ReasoningTokens: usage?.ReasoningTokens,
                TotalTokens: usage?.TotalTokens,
                FinishReason: scanner.FinishReason,
                Response: text.ToString(),
                RequestBodyJson: requestJson,
                ErrorKind: null,
                Message: null,
                OffendingParameter: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            clock.Stop();
            return Failed(
                clock, requestJson, "Timeout",
                $"No response within {config.TimeoutSeconds}s.", null, null);
        }
        catch (HttpRequestException ex)
        {
            clock.Stop();
            return Failed(clock, requestJson, "Transport", ex.Message, null, null);
        }
    }

    private static LlmTestOutcome Failed(
        Stopwatch clock, string requestJson, string errorKind, string message,
        string? offendingParameter, int? httpStatus) =>
        new(
            Ok: false, HttpStatus: httpStatus, TtftMs: null, DurationMs: (int)clock.ElapsedMilliseconds,
            PromptTokens: null, CompletionTokens: null, ReasoningTokens: null, TotalTokens: null,
            FinishReason: null, Response: null, RequestBodyJson: requestJson,
            ErrorKind: errorKind, Message: message, OffendingParameter: offendingParameter);

    /// <summary>The content delta on one SSE line, and whether the terminal marker was seen.</summary>
    private static string? ParseDelta(string line, out bool done)
    {
        done = false;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

        var data = line["data:".Length..].Trim();
        if (data.Length == 0) return null;
        if (data == "[DONE]") { done = true; return null; }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }
        catch (JsonException)
        {
            // A malformed chunk costs one delta, not the whole measurement.
        }

        return null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            // The status code is the diagnosis that matters; failing to read the body must not replace it.
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
