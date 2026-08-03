using Sentry;   // SentryEvent

namespace Diariz.Api.Services;

/// <summary>Redacts credentials and meeting content from telemetry events before they leave the
/// process. Distinct from <see cref="LogSanitizer"/>, which defends against log-injection in log
/// lines rather than against disclosure.
///
/// Transcripts, summaries and minutes are this application's payload, and an event that leaks one
/// cannot be un-sent - so this denies by default and keeps only the identifiers needed to diagnose
/// a failure.</summary>
public static class SentryScrubber
{
    public const string Redacted = "[redacted]";

    // ---- Cross-runtime deny-list ----------------------------------------------------------------
    // KEEP IN SYNC WITH:
    //   - src/Diariz.Worker/telemetry.py (_DENY_EXACT / _DENY_SUBSTRING)
    //   - apps/web/src/lib/scrub.ts (DENY_EXACT / DENY_SUBSTRING)
    // The worker, the API and the browser SPA report to the same GlitchTip instance and must redact
    // the same names. These lists HAVE silently diverged before: "embedding"/"embeddings" (the ECAPA
    // voiceprint vectors) were added to Python only - even though the API is the runtime that
    // actually stores them - while "note"/"notes" existed only here.
    //
    // What the guard-rail is, honestly: each runtime has a test that pins this shared set
    // (SentryScrubberTests.IsSensitiveKey_CoversEveryNameInTheCrossRuntimeDenyList here,
    // test_the_shared_cross_runtime_deny_list_is_covered in tests/test_telemetry.py there, and
    // scrub.test.ts's isSensitiveKey suite in the browser). That catches a REMOVAL from any one
    // copy. It cannot catch an ADDITION made to only one copy - three tests in three languages have
    // no shared source of truth - so adding a name here means adding it to the other two, and to
    // their tests.

    // Exact field names that carry meeting content or biometrics.
    private static readonly HashSet<string> DenyExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "transcript", "transcription", "segments", "words", "summary",
        "minutes", "note", "notes", "content", "authorization", "cookie", "cookies",
        // ECAPA voiceprint vectors (biometric data identifying a speaker by voice). Speaker.Embedding
        // is a vector(192) column on this side, so an entity dumped into Extra would carry one.
        "embedding", "embeddings",
    };

    // Substrings marking a credential regardless of the surrounding name.
    private static readonly string[] DenySubstring =
        ["secret", "token", "password", "apikey", "api_key", "accesskey", "access_key"];

    /// <summary>True when a field with this name must never leave the process.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (DenyExact.Contains(key)) return true;
        foreach (var marker in DenySubstring)
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>SDK hook for <c>SentryOptions.SetBeforeSend</c>: redact in place and return the event.</summary>
    public static SentryEvent? Scrub(SentryEvent e)
    {
        ScrubEventLike(e);

        // Breadcrumbs are NOT redacted here - see ScrubBreadcrumb below for why, and where that
        // redaction actually happens.
        return e;
    }

    /// <summary>SDK hook for <c>SentryOptions.SetBeforeSendTransaction</c>.
    ///
    /// <c>BeforeSend</c> does <b>not</b> run for transactions in the .NET SDK, so without this hook
    /// every redaction in <see cref="Scrub"/> was bypassed on the transaction path - and that path
    /// fires on <b>every request</b> at the configured <c>TracesSampleRate</c>, not only when
    /// something throws. Verified against the installed Sentry 6.8.0 assembly by capturing a real
    /// envelope: the transaction carried <c>"request":{"query_string":"access_token=..."}</c>,
    /// unscrubbed <c>tags</c>, and span descriptions of the form
    /// <c>"POST https://hooks.example/x?token=..."</c>.
    ///
    /// <see cref="SentryEvent"/> and <see cref="SentryTransaction"/> both implement
    /// <see cref="IEventLike"/> (which carries the settable <c>Request</c> plus
    /// <c>Tags</c>/<c>SetTag</c>/<c>Extra</c>/<c>SetExtra</c>), so
    /// <see cref="ScrubEventLike"/> covers both shapes from one place. On a transaction <c>Extra</c>
    /// and <c>Data</c> are the same backing store, so redacting through the interface covers
    /// both.</summary>
    public static SentryTransaction? ScrubTransaction(SentryTransaction transaction)
    {
        ScrubEventLike(transaction);

        // A transaction is itself a span and also carries child spans. Sentry's automatic
        // IHttpClientFactory instrumentation names an outbound span "<METHOD> <url>", and
        // WebhookDeliveryWorker posts to user-supplied URLs - so a token in a webhook's query
        // string arrives here as a span description. SentrySpan.Description has a public setter,
        // so these can be redacted in place (unlike a Breadcrumb - see ScrubBreadcrumb).
        transaction.Description = ScrubSpanDescription(transaction.Description);
        foreach (var span in transaction.Spans)
            span.Description = ScrubSpanDescription(span.Description);

        return transaction;
    }

    /// <summary>Strip the query string from any URL inside a span description, leaving the rest of
    /// the description intact. Descriptions are free text, and the ones that carry a URL are
    /// usually "<c>METHOD url</c>", so each whitespace-separated token is passed through the same
    /// <see cref="StripQueryString"/> used for <c>Request.Url</c>; a token that is not an absolute
    /// URL is returned unchanged, which leaves SQL and other descriptions alone.</summary>
    public static string? ScrubSpanDescription(string? description)
    {
        // No '?' means no query string to strip - and skips the split for the common case.
        if (string.IsNullOrEmpty(description) || !description.Contains('?')) return description;

        var parts = description.Split(' ');
        for (var i = 0; i < parts.Length; i++) parts[i] = StripQueryString(parts[i]);
        return string.Join(' ', parts);
    }

    /// <summary>Return <paramref name="value"/> without its query string when it is an absolute URL
    /// that has one, else unchanged. Shared by <c>Request.Url</c> and span descriptions so there is
    /// only ever one copy of this rule.
    ///
    /// Only recognises absolute URLs by design, not oversight: every caller here passes one.
    /// <c>Request.Url</c> is populated by Sentry.AspNetCore as scheme://host+path; the SignalR
    /// <c>?access_token=</c> case is defended separately by nulling <c>Request.QueryString</c> above;
    /// and span descriptions come only from <c>IHttpClientFactory</c> instrumentation, which resolves
    /// <c>BaseAddress</c> + a relative URI into an absolute one before any handler sees it. A relative
    /// path is never seen here - unlike the browser SPA's copy of this function
    /// (<c>apps/web/src/lib/scrub.ts</c>), which does need to handle one.</summary>
    private static string StripQueryString(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Query)
            ? url.GetLeftPart(UriPartial.Path)
            : value;

    /// <summary>Redact the parts an event and a transaction have in common - Tags, Extra and the
    /// captured request. <see cref="SentryEvent"/> and <see cref="SentryTransaction"/> reach this
    /// through <see cref="IEventLike"/>; both are mutated in place.</summary>
    private static void ScrubEventLike(IEventLike e)
    {
        foreach (var key in e.Extra.Keys.ToList())
            if (IsSensitiveKey(key)) e.SetExtra(key, Redacted);

        foreach (var key in e.Tags.Keys.ToList())
            if (IsSensitiveKey(key)) e.SetTag(key, Redacted);

        var request = e.Request;
        if (request is not null)
        {
            foreach (var key in request.Headers.Keys.ToList())
                if (IsSensitiveKey(key)) request.Headers[key] = Redacted;

            // The query string and cookie header are one opaque string each, not a dictionary of
            // named fields - a key-based deny-list can never reach into them. Concretely: the
            // SignalR hub takes its JWT as a query parameter (browsers can't set an Authorization
            // header on a WebSocket handshake), so `?access_token=<JWT>` is the normal path here,
            // not an edge case. Drop both outright.
            request.QueryString = null;
            request.Cookies = null;

            // Request.Url is populated by Sentry.AspNetCore as scheme://host+path (the query
            // string is a separate field, above) - but don't assume every caller does the same.
            // Strip any query portion defensively rather than nulling a path that carries no
            // credential and is genuinely useful for diagnosis.
            if (!string.IsNullOrEmpty(request.Url)) request.Url = StripQueryString(request.Url);

            // Request bodies can contain anything a user typed or dictated. Never send one.
            request.Data = null;
        }
    }

    /// <summary>SDK hook for <c>SentryOptions.SetBeforeBreadcrumb</c> (wired in Task 7's
    /// Program.cs, alongside <see cref="Scrub"/> on <c>SetBeforeSend</c>).
    ///
    /// Sentry.Extensions.Logging - shipped inside Sentry.AspNetCore and on by default - turns
    /// every <c>ILogger</c> call at Information+ into a breadcrumb carrying a <c>Data</c>
    /// dictionary of the same shape as <c>Extra</c>, so it needs the same redaction. But a
    /// breadcrumb cannot be redacted the way <see cref="Scrub"/> redacts a <see cref="SentryEvent"/>:
    /// once attached to an event, <see cref="Breadcrumb.Data"/> has no accessible setter (it is
    /// assigned once, via the constructor) and <c>SentryEvent.Breadcrumbs</c> exposes no way to
    /// replace an entry - confirmed against the installed Sentry 6.8.0 assembly, not assumed.
    /// <c>SetBeforeBreadcrumb</c> runs earlier, before a breadcrumb is frozen into the event, and
    /// - unlike <see cref="Scrub"/> - works by returning a replacement rather than mutating in
    /// place.
    ///
    /// Known, accepted side effect: <see cref="Breadcrumb"/>'s public constructor takes no
    /// timestamp, so a <b>redacted</b> breadcrumb is stamped with the time of the rebuild rather
    /// than of the original event. The drift is sub-millisecond and unavoidable through the public
    /// API; untouched breadcrumbs keep their original timestamp.</summary>
    public static Breadcrumb? ScrubBreadcrumb(Breadcrumb breadcrumb)
    {
        if (breadcrumb.Data is null) return breadcrumb;

        Dictionary<string, string>? redacted = null;
        foreach (var (key, value) in breadcrumb.Data)
        {
            if (!IsSensitiveKey(key)) continue;
            redacted ??= new Dictionary<string, string>(breadcrumb.Data);
            redacted[key] = Redacted;
        }
        if (redacted is null) return breadcrumb;

        return new Breadcrumb(breadcrumb.Message ?? "", breadcrumb.Type ?? "default", redacted, breadcrumb.Category, breadcrumb.Level);
    }
}
