namespace Diariz.Api.Services.Llm;

/// <summary>Retries a model call that failed for a reason likely to clear on its own.
///
/// <para>WHY THIS EXISTS: a single refusal from the endpoint used to be the last word. The processors
/// catch, set <c>RecordingStatus.Failed</c>, and the workers ack the stream entry in a <c>finally</c> so a
/// poison message cannot loop - between them there was no attempt boundary anywhere, and a blip lasting one
/// second cost the recording its summary until somebody re-ran it by hand. The usage log shows what these
/// blips look like: refusals returned in 7 to 16 milliseconds, far too fast to be the model doing work,
/// against the same endpoint and model that answered the identical request minutes later.</para>
///
/// <para>WHY A HANDLER rather than a loop in each processor: it is one place for all ten LLM clients, and
/// registering it OUTSIDE <see cref="LlmTelemetryHandler"/> means every attempt becomes its own row in the
/// usage log. A retry that hid its attempts would make the log lie about how much the platform is
/// spending.</para></summary>
public sealed class LlmRetryHandler : DelegatingHandler
{
    /// <summary>Total attempts, not retries: 3 means the original plus two more.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Waits between attempts, so <see cref="MaxAttempts"/> adds at most ten seconds to a call.
    ///
    /// <para>Deliberately modest. It has to cover the short refusals above without leaving an interactive
    /// chat turn hanging, since the same clients serve both. A longer outage - the endpoint down for
    /// minutes - still fails the recording, and re-running it remains the recovery for that.</para></summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];

    /// <summary>The statuses no retry can help. A wrong API key, a wrong endpoint URL, or a body the server
    /// will never accept are settled the moment they are answered, and retrying them only delays the error
    /// an administrator is waiting to read.</summary>
    private static readonly HashSet<int> Permanent = [401, 403, 404, 413, 422];

    private readonly ILogger<LlmRetryHandler> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Injectable purely so tests exercise the real backoff decision without sleeping
    /// through it; production always passes null and gets
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. One constructor with a default rather than
    /// two overloads, so DI has nothing to choose between - see <c>StreamReclaimer</c>, which takes its
    /// thresholds the same way and for the same reason.</param>
    public LlmRetryHandler(
        ILogger<LlmRetryHandler> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Whether a failed call is worth trying again.
    ///
    /// <para>400 counts. That looks wrong - a Bad Request is the definition of a request that will not
    /// improve by being repeated - but an OpenAI-compatible server is not consistent about which status it
    /// uses for a transient condition: LM Studio answers "model is not loaded" with a 400 while it is
    /// JIT-loading or swapping the model, and those are exactly the failures this exists for. Classifying
    /// by status alone would therefore exclude the reported bug. The cost of being wrong is two extra
    /// attempts and ten seconds, after which the call fails with the endpoint's own message unchanged.</para></summary>
    public static bool IsTransient(int status) => status >= 400 && !Permanent.Contains(status);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Only a JSON body is re-sendable. The dictation upload is MultipartFormDataContent wrapping a
        // stream of up to 10 MiB of audio: buffering it to enable a retry would defeat the streaming
        // upload for the sake of a second attempt, so that call keeps its single shot - the same line
        // LlmTelemetryHandler draws, for the same reason.
        byte[]? body = null;
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentType?.MediaType is not "application/json")
                return await base.SendAsync(request, ct);

            // Captured BEFORE the first send: HttpClient may dispose the request content once the call
            // completes, and by then there is nothing left to copy.
            body = await request.Content.ReadAsByteArrayAsync(ct);
        }

        var target = request.RequestUri?.GetLeftPart(UriPartial.Path) ?? "(no uri)";

        for (var attempt = 1; ; attempt++)
        {
            // Attempt 1 sends the caller's own request; only a retry needs a copy, because an
            // HttpRequestMessage cannot be sent twice.
            var outbound = attempt == 1 ? request : Clone(request, body);

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(outbound, ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    ex, "LLM call to {Target} could not connect (attempt {Attempt} of {Max}); retrying",
                    target, attempt, MaxAttempts);
                await _delay(Backoff[attempt - 1], ct);
                continue;
            }

            if (attempt >= MaxAttempts || !IsTransient((int)response.StatusCode)) return response;

            _log.LogWarning(
                "LLM call to {Target} returned {Status} (attempt {Attempt} of {Max}); retrying",
                target, (int)response.StatusCode, attempt, MaxAttempts);

            // Nothing has read this body, so dropping it costs nothing. The body of the LAST attempt is
            // the one that survives, which is what LlmResponse reports to the user.
            response.Dispose();
            await _delay(Backoff[attempt - 1], ct);
        }
    }

    /// <summary>A fresh message carrying the same method, URI, headers and bytes - notably the
    /// <c>Authorization</c> header, without which a retry would fail as unauthorised.</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)original.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        if (body is null) return clone;

        clone.Content = new ByteArrayContent(body);
        foreach (var header in original.Content!.Headers)
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
