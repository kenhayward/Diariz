using System.Net;

namespace Diariz.Api.Services;

public sealed record WebhookUrlValidation(bool Ok, string? Reason)
{
    public static readonly WebhookUrlValidation Valid = new(true, null);
    public static WebhookUrlValidation Invalid(string reason) => new(false, reason);
}

public interface IWebhookUrlValidator
{
    Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default);
}

/// <summary>Validates a user-supplied webhook target against SSRF: http(s) only, and every DNS-resolved IP must
/// pass <see cref="UrlFetchGuard.IsBlocked"/> (rejects loopback/private/link-local/CGNAT/metadata). The DNS
/// resolver is injectable for tests.</summary>
public sealed class WebhookUrlValidator : IWebhookUrlValidator
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public WebhookUrlValidator() : this((host, ct) => Dns.GetHostAddressesAsync(host, ct)) { }
    public WebhookUrlValidator(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => _resolve = resolve;

    public async Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return WebhookUrlValidation.Invalid("Enter a valid URL.");
        if (!UrlFetchGuard.IsAllowedScheme(uri))
            return WebhookUrlValidation.Invalid("The URL must start with http:// or https://.");

        IPAddress[] ips;
        try { ips = await _resolve(uri.DnsSafeHost, ct); }
        catch { return WebhookUrlValidation.Invalid("Could not resolve that host."); }

        if (ips.Length == 0 || ips.Any(UrlFetchGuard.IsBlocked))
            return WebhookUrlValidation.Invalid("That address is not allowed.");

        return WebhookUrlValidation.Valid;
    }
}
