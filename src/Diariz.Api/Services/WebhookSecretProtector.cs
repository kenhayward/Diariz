using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Services;

public interface IWebhookSecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? ciphertext);
}

/// <summary>Encrypts webhook signing secrets at rest (must be recoverable to sign), mirroring
/// <see cref="ApiKeyProtector"/> with a distinct purpose string.</summary>
public sealed class WebhookSecretProtector : IWebhookSecretProtector
{
    private readonly IDataProtector _protector;

    public WebhookSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Webhooks.SigningSecret");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch { return null; }
    }
}
