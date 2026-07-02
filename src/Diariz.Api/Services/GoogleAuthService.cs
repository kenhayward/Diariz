using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diariz.Api.Configuration;
using Google.Apis.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Validated identity from a Google sign-in (the trustworthy subset of the ID-token claims).</summary>
public record GoogleUserInfo(
    string Subject, string Email, bool EmailVerified, string? Name, string? Picture, string? HostedDomain);

public interface IGoogleAuthService
{
    /// <summary>Whether Google sign-in is configured (a client id + secret are present).</summary>
    bool Enabled { get; }

    /// <summary>Build the Google consent URL for the server-side authorization-code + PKCE flow.</summary>
    string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge);

    /// <summary>Exchange an authorization code (with its PKCE verifier) for tokens and return the validated
    /// ID-token identity. Throws when the exchange fails or the ID token is invalid.</summary>
    Task<GoogleUserInfo> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default);
}

/// <summary>Server-side (confidential-client) Google OAuth: the client secret never leaves the API.
/// The ID token is validated against Google's JWKS with our client id as the audience.</summary>
public class GoogleAuthService : IGoogleAuthService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    // Login only — non-sensitive scopes (no Google security review). Gmail/Calendar are a later phase.
    private const string Scope = "openid email profile";

    private readonly HttpClient _http;
    private readonly GoogleAuthOptions _opts;

    public GoogleAuthService(HttpClient http, IOptions<GoogleAuthOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public bool Enabled => _opts.Enabled;

    public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _opts.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "online",
            ["prompt"] = "select_account",
        };
        return QueryHelpers.AddQueryString(AuthEndpoint, query);
    }

    public async Task<GoogleUserInfo> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _opts.ClientId,
                ["client_secret"] = _opts.ClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier,
            }),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google token exchange failed ({(int)resp.StatusCode}).");

        var token = JsonSerializer.Deserialize<TokenResponse>(body);
        if (string.IsNullOrEmpty(token?.IdToken))
            throw new InvalidOperationException("Google token exchange returned no id_token.");

        // Validates signature (Google JWKS), issuer, expiry, and that the token was minted for our client.
        var payload = await GoogleJsonWebSignature.ValidateAsync(
            token.IdToken, new GoogleJsonWebSignature.ValidationSettings { Audience = [_opts.ClientId] });

        return new GoogleUserInfo(
            payload.Subject, payload.Email ?? "", payload.EmailVerified,
            payload.Name, payload.Picture, payload.HostedDomain);
    }

    private sealed record TokenResponse
    {
        [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    }
}

/// <summary>PKCE + anti-CSRF state helpers (RFC 7636). The <see cref="Challenge"/> transform is pure and
/// deterministic (unit-tested against the RFC vector); the verifier/state generators use a CSPRNG.</summary>
public static class OAuthPkce
{
    /// <summary>A high-entropy code verifier (43-char base64url of 32 random bytes).</summary>
    public static string NewCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>An opaque anti-CSRF state value.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge: base64url(SHA-256(verifier)).</summary>
    public static string Challenge(string codeVerifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
