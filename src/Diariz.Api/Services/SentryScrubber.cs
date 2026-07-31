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

        foreach (var key in e.Request.Headers.Keys.ToList())
            if (IsSensitiveKey(key)) e.Request.Headers[key] = Redacted;

        // Request bodies can contain anything a user typed or dictated. Never send one.
        e.Request.Data = null;

        return e;
    }
}
