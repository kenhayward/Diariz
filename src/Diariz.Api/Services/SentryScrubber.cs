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

    // Exact field names that carry meeting content.
    private static readonly HashSet<string> DenyExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "transcript", "transcription", "segments", "words", "summary",
        "minutes", "note", "notes", "content", "authorization", "cookie", "cookies",
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

    /// <summary>SDK hook: redact in place and return the event.</summary>
    public static SentryEvent? Scrub(SentryEvent e)
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
            if (!string.IsNullOrEmpty(request.Url) &&
                Uri.TryCreate(request.Url, UriKind.Absolute, out var url) &&
                !string.IsNullOrEmpty(url.Query))
            {
                request.Url = url.GetLeftPart(UriPartial.Path);
            }

            // Request bodies can contain anything a user typed or dictated. Never send one.
            request.Data = null;
        }

        // Breadcrumbs are NOT redacted here - see ScrubBreadcrumb below for why, and where that
        // redaction actually happens.
        return e;
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
    /// place.</summary>
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
