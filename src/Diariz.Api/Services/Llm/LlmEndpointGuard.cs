using System.Net;
using System.Net.Sockets;

namespace Diariz.Api.Services.Llm;

/// <summary>Where the server's own LLM calls - summaries, chat, embeddings, dictation, OCR, the admin model test and
/// model discovery - are allowed to connect.
///
/// <para>These URLs come from configuration an administrator or an ordinary user controls (a user can set their own
/// summarisation endpoint), and the server sends transcript content to them and shows the reply back. So the
/// destination matters. Private ranges stay reachable on purpose: self-hosted model servers on the LAN are the
/// normal deployment. What is refused is never a model server: link-local and cloud-metadata addresses, unusable
/// addresses, and - outside Development - the API's own loopback, where nothing but the API itself is listening.</para>
///
/// <para>The check runs in the socket connect step, on the addresses a host name actually resolved to, so neither a
/// DNS name pointing inward nor a redirect to one gets past it.</para></summary>
public static class LlmEndpointGuard
{
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("fd00:ec2::254"),   // AWS instance metadata over IPv6
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud metadata
        IPAddress.Parse("168.63.129.16"),   // Azure wire server
    ];

    public static bool IsBlocked(IPAddress ip, bool allowLoopback)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return !allowLoopback;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (MetadataAddresses.Any(ip.Equals)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16 link-local, incl. 169.254.169.254 metadata
            if (b[0] == 0) return true;                  // 0.0.0.0/8
            if (b[0] >= 224) return true;                // multicast / reserved
            return false;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast;
    }

    /// <summary>The host's addresses, refused if any is blocked.</summary>
    public static async Task<IPAddress[]> ResolveAllowedAsync(string host, bool allowLoopback, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0 || addresses.Any(a => IsBlocked(a, allowLoopback)))
            throw new HttpRequestException(
                $"Connecting to '{host}' is not allowed for an LLM endpoint (link-local, metadata or loopback address).");
        return addresses;
    }

    /// <summary>A <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the host, refuses the connection if
    /// any resolved address is blocked, and otherwise connects - so every hop of a redirect is checked too.</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback(bool allowLoopback) =>
        async (context, ct) =>
        {
            var addresses = await ResolveAllowedAsync(context.DnsEndPoint.Host, allowLoopback, ct);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
}

/// <summary>Checks the request's own host before it is sent. Needed alongside <see cref="LlmEndpointGuard.ConnectCallback"/>
/// because when the API reaches the internet through an HTTP proxy the socket connects to the proxy, and the
/// connect-step check never sees the real destination.</summary>
public sealed class LlmEndpointGuardHandler(bool allowLoopback) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
            await LlmEndpointGuard.ResolveAllowedAsync(uri.DnsSafeHost, allowLoopback, ct);
        return await base.SendAsync(request, ct);
    }
}
