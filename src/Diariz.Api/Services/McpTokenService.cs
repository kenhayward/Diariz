using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Services;

/// <summary>A freshly minted MCP token: the plaintext <paramref name="Token"/> (shown to the user exactly
/// once), its <paramref name="Hash"/> (what we persist), and a short non-secret <paramref name="Prefix"/>
/// for display in the settings list.</summary>
public sealed record GeneratedMcpToken(string Token, string Hash, string Prefix);

public interface IMcpTokenService
{
    /// <summary>Mints a new random token and returns it alongside its hash + display prefix.</summary>
    GeneratedMcpToken Generate();
}

/// <summary>Generates and hashes MCP personal access tokens. Tokens are <c>dz_mcp_</c> + base64url(32 random
/// bytes); only the SHA-256 hash is stored, so an incoming token is verified by hashing and looking it up —
/// the secret is never persisted or recoverable. Hashing is pure/static so it can be reused by the auth
/// handler and unit-tested without state.</summary>
public sealed class McpTokenService : IMcpTokenService
{
    /// <summary>Identifiable, secret-scanning-friendly prefix on every token.</summary>
    public const string TokenPrefix = "dz_mcp_";

    /// <summary>Length of the non-secret display prefix stored per token (the prefix + 6 chars).</summary>
    public const int DisplayPrefixLength = 13;

    public GeneratedMcpToken Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = TokenPrefix + Base64UrlEncode(bytes);
        return new GeneratedMcpToken(token, Hash(token), DisplayPrefix(token));
    }

    /// <summary>Lowercase-hex SHA-256 (64 chars) of the full token string. Used both when storing a new token
    /// and when verifying an incoming one.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    /// <summary>The short, non-secret leading slice of a token used for display.</summary>
    public static string DisplayPrefix(string token) =>
        string.IsNullOrEmpty(token) || token.Length <= DisplayPrefixLength ? token ?? "" : token[..DisplayPrefixLength];

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
