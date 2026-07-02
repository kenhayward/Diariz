using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Services;

/// <summary>Encrypts/decrypts the stored Google OAuth refresh token at rest, using a dedicated Data-Protection
/// purpose (separate from the summarisation API-key protector). Like <see cref="ApiKeyProtector"/>, it returns
/// null on empty input or a decrypt failure (e.g. a lost keyring) so callers degrade gracefully.</summary>
public interface IGoogleTokenProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? ciphertext);
}

public class GoogleTokenProtector : IGoogleTokenProtector
{
    private readonly IDataProtector _protector;

    public GoogleTokenProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Google.RefreshToken");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch { return null; }
    }
}
