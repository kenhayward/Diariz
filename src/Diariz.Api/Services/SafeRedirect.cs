namespace Diariz.Api.Services;

/// <summary>Guards outbound redirects against open-redirect abuse: a redirect target is honoured only when it
/// is an absolute URL whose host is on an explicit allowlist (the configured public origin, or the trusted
/// request host in dev). Anything else - a foreign host, a relative or malformed value - falls back to a safe
/// local path. Pure and side-effect free.</summary>
public static class SafeRedirect
{
    public static string Within(string targetUrl, IReadOnlyCollection<string> allowedHosts, string fallbackPath = "/")
    {
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
            allowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            return targetUrl;
        return fallbackPath;
    }
}
