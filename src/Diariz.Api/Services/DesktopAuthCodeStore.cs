using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>A redeemed desktop sign-in code: who it was minted for, and the PKCE challenge the
/// initiating desktop app must prove it holds the verifier for.</summary>
public sealed record DesktopAuthTicket(Guid UserId, string Challenge);

/// <summary>Single-use, short-TTL codes that bridge the browser Google callback to the desktop app.
/// The code travels via the diariz:// deep link; redemption is one-shot (defence in depth alongside
/// the PKCE-style verifier check at exchange time).</summary>
public interface IDesktopAuthCodeStore
{
    Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl);
    Task<DesktopAuthTicket?> RedeemAsync(string code);
}

public sealed class RedisDesktopAuthCodeStore(IConnectionMultiplexer redis) : IDesktopAuthCodeStore
{
    private const string Prefix = "desktop-auth-code:";

    public async Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl)
    {
        var code = OAuthPkce.NewState(); // 43-char base64url, URL-safe for the deep link
        // challenge is base64url (OAuthPkce.Challenge) and userId:N is fixed-width hex - neither contains
        // ':', so RedeemAsync's Split(':', 2) round-trips the payload safely.
        var payload = $"{userId:N}:{challenge}";
        await redis.GetDatabase().StringSetAsync(Prefix + code, payload, ttl);
        return code;
    }

    public async Task<DesktopAuthTicket?> RedeemAsync(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        // GETDEL: atomic read-and-delete, so a code redeems exactly once.
        var value = await redis.GetDatabase().StringGetDeleteAsync(Prefix + code);
        if (value.IsNullOrEmpty) return null;
        var parts = ((string)value!).Split(':', 2);
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var uid)
            ? new DesktopAuthTicket(uid, parts[1])
            : null;
    }
}
