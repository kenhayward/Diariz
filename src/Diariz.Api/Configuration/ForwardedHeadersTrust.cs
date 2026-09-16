using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Diariz.Api.Configuration;

/// <summary>Which peers may tell the API the original scheme and client address (X-Forwarded-Proto/For).
///
/// <para>The reverse proxy (the web container's nginx, and any TLS terminator in front of it) reaches the API over a
/// private container network, so only loopback and private ranges are trusted. Clearing the allowlist instead - the
/// previous setting - does not mean "trust the proxy", it means trust every peer, including one that reaches the
/// API's port directly and simply claims https.</para>
///
/// <para>The IPv4 ranges cover every Docker default address pool; the middleware maps an IPv4-mapped IPv6 peer back
/// to IPv4 before checking.</para></summary>
public static class ForwardedHeadersTrust
{
    private static readonly string[] TrustedNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", // loopback + RFC 1918
        "::1/128", "fc00::/7",                                          // IPv6 loopback + unique-local
    ];

    public static void Configure(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var network in TrustedNetworks) o.KnownIPNetworks.Add(IPNetwork.Parse(network));
    }
}
