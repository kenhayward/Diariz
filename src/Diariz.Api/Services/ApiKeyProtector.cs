using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Services;

/// <summary>Encrypts/decrypts user-supplied API keys for storage at rest.</summary>
public interface IApiKeyProtector
{
    /// <summary>Encrypts a key for storage. Returns null for null/empty input.</summary>
    string? Protect(string? plaintext);
    /// <summary>Decrypts a stored key. Returns null for null/empty input or if decryption fails
    /// (e.g. the Data Protection keyring was lost), degrading gracefully to "not set".</summary>
    string? Unprotect(string? ciphertext);
}

public class ApiKeyProtector : IApiKeyProtector
{
    private readonly IDataProtector _protector;

    public ApiKeyProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Summarization.ApiKey");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch { return null; }
    }
}
