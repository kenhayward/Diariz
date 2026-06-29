using System.Net;
using System.Net.Sockets;

namespace Diariz.Api.Services;

public interface IUrlFetcher
{
    /// <summary>Fetch a URL attachment and return its text (HTML reduced to plain text), or null when the
    /// URL is disallowed, unreachable, or too large.</summary>
    Task<string?> FetchTextAsync(string url, CancellationToken ct = default);
}

/// <summary>Pure SSRF guards for outbound URL fetches — no IO, so they're unit-testable.</summary>
public static class UrlFetchGuard
{
    public static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>True for addresses that must never be fetched (loopback, link-local, private/ULA ranges,
    /// multicast, unspecified) — i.e. anything that could reach internal services.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10.0.0.0/8
            if (b[0] == 127) return true;                            // 127.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;             // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;             // 169.254.0.0/16 link-local
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // 100.64.0.0/10 CGNAT
            if (b[0] >= 224) return true;                            // multicast / reserved
            if (b[0] == 0) return true;                              // 0.0.0.0/8
            return false;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
        var bytes = ip.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC) return true;                  // fc00::/7 unique-local
        return false;
    }
}

/// <summary>Fetches a URL attachment's text for chat context, behind SSRF guards (scheme + resolved-IP
/// allow-list), a redirect limit re-validated each hop, a size cap, and a timeout.</summary>
public sealed class UrlFetcher : IUrlFetcher
{
    private const int MaxBytes = 2 * 1024 * 1024; // 2 MB
    private const int MaxRedirects = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<UrlFetcher> _logger;

    public UrlFetcher(IHttpClientFactory factory, ILogger<UrlFetcher> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string?> FetchTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var token = cts.Token;

            var current = new Uri(url, UriKind.Absolute);
            using var client = _factory.CreateClient("url-attachments"); // handler has AllowAutoRedirect=false

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!await IsHostAllowedAsync(current, token)) return null;

                using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    current = new Uri(current, loc); // resolve relative redirects; re-checked next loop
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return null;

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                var bytes = await ReadCappedAsync(resp, token);
                if (bytes is null) return null;
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                return contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? HtmlText.ToPlainText(text)
                    : text.Trim();
            }
            return null; // too many redirects
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            _logger.LogInformation(ex, "URL attachment fetch failed for {Url}", url);
            return null;
        }
    }

    /// <summary>Allow only http(s) URLs whose every resolved IP is public.</summary>
    private static async Task<bool> IsHostAllowedAsync(Uri uri, CancellationToken ct)
    {
        if (!UrlFetchGuard.IsAllowedScheme(uri)) return false;
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
        catch { return false; }
        return addresses.Length > 0 && addresses.All(a => !UrlFetchGuard.IsBlocked(a));
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is > MaxBytes) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBytes) return null; // over the cap
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
