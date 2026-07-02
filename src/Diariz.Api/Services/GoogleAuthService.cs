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

/// <summary>The token set returned by an authorization-code exchange. <c>RefreshToken</c> is present only
/// when offline access was requested with a fresh consent (<c>access_type=offline&amp;prompt=consent</c>).</summary>
public record GoogleTokens(string? AccessToken, string? RefreshToken, int ExpiresInSeconds, string? Scope, string? IdToken);

public interface IGoogleAuthService
{
    /// <summary>Whether Google sign-in is configured (a client id + secret are present).</summary>
    bool Enabled { get; }

    /// <summary>Build the Google consent URL. <paramref name="offline"/> requests a refresh token
    /// (<c>access_type=offline</c> + <c>prompt=consent</c> + <c>include_granted_scopes</c>) for the
    /// incremental data-access flow; sign-in uses <c>offline=false</c>.</summary>
    string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, string scope, bool offline);

    /// <summary>Exchange an authorization code (with its PKCE verifier) for the full token set.</summary>
    Task<GoogleTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default);

    /// <summary>Refresh an access token from a stored refresh token, or throw on failure (e.g. revoked).</summary>
    Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Validate a Google ID token (signature via JWKS, audience = our client id) → identity.</summary>
    Task<GoogleUserInfo> ValidateIdTokenAsync(string idToken);

    /// <summary>Best-effort revoke of a refresh/access token at Google. Never throws.</summary>
    Task RevokeAsync(string token, CancellationToken ct = default);
}

/// <summary>Server-side (confidential-client) Google OAuth: the client secret never leaves the API.</summary>
public class GoogleAuthService : IGoogleAuthService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Sign-in scopes (non-sensitive — no Google review).</summary>
    public const string SignInScope = "openid email profile";
    /// <summary>Google Calendar read-only (sensitive scope).</summary>
    public const string CalendarReadScope = "https://www.googleapis.com/auth/calendar.readonly";
    /// <summary>Gmail draft/compose (sensitive scope).</summary>
    public const string GmailComposeScope = "https://www.googleapis.com/auth/gmail.compose";

    private readonly HttpClient _http;
    private readonly GoogleAuthOptions _opts;

    public GoogleAuthService(HttpClient http, IOptions<GoogleAuthOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public bool Enabled => _opts.Enabled;

    public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, string scope, bool offline)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _opts.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Offline: force a consent so Google returns a refresh token, and keep prior grants (incremental).
            ["access_type"] = offline ? "offline" : "online",
            ["prompt"] = offline ? "consent" : "select_account",
        };
        if (offline) query["include_granted_scopes"] = "true";
        return QueryHelpers.AddQueryString(AuthEndpoint, query);
    }

    public async Task<GoogleTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _opts.ClientId,
            ["client_secret"] = _opts.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        }, ct);
    }

    public async Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _opts.ClientId,
            ["client_secret"] = _opts.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);
    }

    private async Task<GoogleTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            // Include Google's error body (e.g. invalid_client / invalid_grant) — the actual cause, logged by the caller.
            throw new InvalidOperationException($"Google token request failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");

        var t = JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("Google token request returned no body.");
        return new GoogleTokens(t.AccessToken, t.RefreshToken, t.ExpiresIn, t.Scope, t.IdToken);
    }

    public async Task<GoogleUserInfo> ValidateIdTokenAsync(string idToken)
    {
        var payload = await GoogleJsonWebSignature.ValidateAsync(
            idToken, new GoogleJsonWebSignature.ValidationSettings { Audience = [_opts.ClientId] });
        return new GoogleUserInfo(
            payload.Subject, payload.Email ?? "", payload.EmailVerified,
            payload.Name, payload.Picture, payload.HostedDomain);
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            };
            using var _ = await _http.SendAsync(req, ct);
        }
        catch { /* best-effort — the local token is cleared regardless */ }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
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
