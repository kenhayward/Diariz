using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Services;

/// <summary>A freshly minted API token: plaintext <paramref name="Token"/> (shown once), its
/// <paramref name="Hash"/> (persisted), and a short non-secret <paramref name="Prefix"/> for display.</summary>
public sealed record GeneratedApiToken(string Token, string Hash, string Prefix);

public interface IApiTokenService
{
    /// <summary>Mints a new random token and returns it alongside its hash + display prefix.</summary>
    GeneratedApiToken Generate();
}

/// <summary>Generates and hashes personal REST-API tokens: <c>dz_api_</c> + base64url(32 random bytes),
/// stored only as a lowercase-hex SHA-256 hash (verified by hashing an incoming token and looking it up).
/// Hashing is pure/static so the auth handler can reuse it and it can be unit-tested without state.</summary>
public sealed class ApiTokenService : IApiTokenService
{
    /// <summary>Identifiable, secret-scanning-friendly prefix on every token.</summary>
    public const string TokenPrefix = "dz_api_";

    /// <summary>Length of the non-secret display prefix stored per token (the prefix + 6 chars).</summary>
    public const int DisplayPrefixLength = 13;

    public GeneratedApiToken Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = TokenPrefix + Base64UrlEncode(bytes);
        return new GeneratedApiToken(token, Hash(token), DisplayPrefix(token));
    }

    /// <summary>Lowercase-hex SHA-256 (64 chars) of the full token string.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    /// <summary>The short, non-secret leading slice of a token used for display.</summary>
    public static string DisplayPrefix(string token) =>
        string.IsNullOrEmpty(token) || token.Length <= DisplayPrefixLength ? token ?? "" : token[..DisplayPrefixLength];

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
