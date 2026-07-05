using System.Net;
using System.Net.Sockets;

namespace Diariz.Api.Services;

/// <summary>SSRF guard for user-supplied external <c>.ics</c> feed URLs. A feed URL is fetched server-side, so
/// without guarding it a user could point it at internal services (<c>169.254.169.254</c> cloud metadata,
/// <c>localhost</c>, RFC-1918 hosts). <see cref="ValidateSyntax"/> is the pure, DNS-free gate (https only, no
/// private/loopback literals); the fetcher additionally resolves the host and re-checks every resolved IP with
/// <see cref="IsBlockedAddress"/> to defeat DNS rebinding.</summary>
public static class IcsUrlGuard
{
    /// <summary>Reject anything that isn't a plain <c>https</c> URL to a public host, by inspection alone (no
    /// DNS). Returns <c>(true, null)</c> when the URL is syntactically safe, else <c>(false, reason)</c>.</summary>
    public static (bool Ok, string? Error) ValidateSyntax(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "A calendar URL is required.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "That is not a valid URL.");

        if (uri.Scheme != Uri.UriSchemeHttps)
            return (false, "Only https calendar URLs are allowed.");

        if (string.IsNullOrEmpty(uri.Host))
            return (false, "The URL has no host.");

        // IP literals: block private/loopback/special ranges outright.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            // uri.Host keeps IPv6 in bracket-less form already (e.g. "::1").
            if (IPAddress.TryParse(uri.Host, out var literal) && IsBlockedAddress(literal))
                return (false, "That address is not allowed.");
            return (true, null);
        }

        // DNS names: block obvious loopback/mDNS names. The real defence is the resolved-IP re-check at fetch time.
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local"))
            return (false, "That host is not allowed.");

        return (true, null);
    }

    /// <summary>True when <paramref name="ip"/> is loopback, private (RFC-1918), link-local, unique-local,
    /// CGNAT, multicast, or the unspecified address - i.e. anything the fetcher must refuse to connect to.</summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        // Normalise IPv4-mapped IPv6 (::ffff:10.0.0.1) down to IPv4 so the v4 rules apply.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;                 // 127.0.0.0/8, ::1
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true; // 0.0.0.0, ::

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                        // 0.0.0.0/8 "this network"
                || b[0] == 10                                       // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)        // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                     // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                     // 169.254.0.0/16 link-local (incl. cloud metadata)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       // 100.64.0.0/10 CGNAT
                || b[0] >= 224;                                     // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return true; // fe80::/10, ff00::/8
            var first = ip.GetAddressBytes()[0];
            return (first & 0xFE) == 0xFC;                          // fc00::/7 unique-local (fc/fd)
        }

        return false;
    }
}
