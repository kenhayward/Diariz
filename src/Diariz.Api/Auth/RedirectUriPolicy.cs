namespace Diariz.Api.Auth;

/// <summary>Gates which <c>redirect_uri</c>s an MCP client may register via Dynamic Client Registration
/// (RFC 7591). OpenIddict 7.x has no native DCR endpoint, so registration is handled by our own controller and
/// this allowlist is the security boundary: only absolute HTTPS URIs whose host is explicitly permitted are
/// accepted (loopback hosts may use http, per OAuth 2.1), and a fragment is never allowed. Pure/static so it is
/// unit-tested without a request.</summary>
public static class RedirectUriPolicy
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>True if <paramref name="redirectUri"/> is an absolute http(s) URI with no fragment whose host is
    /// on <paramref name="allowedHosts"/> (case-insensitive), and, when the scheme is http, the host is a
    /// loopback address. Everything else - relative URIs, unknown hosts, non-loopback http, other schemes,
    /// fragments, malformed input - is rejected.</summary>
    public static bool IsAllowed(string? redirectUri, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(redirectUri)) return false;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !isHttp) return false;

        var host = uri.Host;
        var isLoopback = LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        // http is only permitted for loopback callbacks (Desktop/Code); everything public must be https.
        if (isHttp && !isLoopback) return false;

        return allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True only if the set is non-empty and every URI passes <see cref="IsAllowed"/> - a single
    /// disallowed <c>redirect_uri</c> rejects the whole registration.</summary>
    public static bool AllAllowed(IReadOnlyCollection<string> redirectUris, IReadOnlyCollection<string> allowedHosts) =>
        redirectUris.Count > 0 && redirectUris.All(u => IsAllowed(u, allowedHosts));
}
