using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Auth;

/// <summary>The decision carried by a validated consent ticket: which user consented, and whether they allowed
/// or denied the connection.</summary>
public sealed record ConsentDecision(Guid UserId, bool Allow);

/// <summary>Bridges the SPA's JWT session to the browser-redirect OAuth authorize step. The SPA consent screen
/// (authenticated with the normal JWT) records the user's allow/deny decision in a short-lived, encrypted
/// cookie; the subsequent top-level navigation to <c>/connect/authorize</c> (which carries no Authorization
/// header) reads it back. The ticket is protected with ASP.NET Data Protection, bound to the specific
/// <c>client_id</c> and given a short expiry, so it cannot be forged, replayed against a different client, or
/// used after it goes stale.</summary>
public interface IOAuthConsentTicketProtector
{
    /// <summary>Encrypt a consent decision into an opaque cookie value.</summary>
    string Issue(Guid userId, string clientId, bool allow, DateTimeOffset expiresAt);

    /// <summary>Decrypt and validate a cookie value against the current request's <paramref name="clientId"/>
    /// and <paramref name="now"/>. Returns the decision, or null if the cookie is missing, tampered, minted by
    /// another keyring, bound to a different client, or expired.</summary>
    ConsentDecision? Verify(string? cookieValue, string clientId, DateTimeOffset now);
}

public sealed class OAuthConsentTicketProtector : IOAuthConsentTicketProtector
{
    /// <summary>The cookie name the authorize endpoint reads and the consent endpoint sets.</summary>
    public const string CookieName = "diariz_oauth_consent";

    private readonly IDataProtector _protector;

    public OAuthConsentTicketProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Mcp.OAuthConsent.v1");

    public string Issue(Guid userId, string clientId, bool allow, DateTimeOffset expiresAt)
    {
        // userId | clientId | allow(0/1) | expiryUnixSeconds. clientId is our own GUID-hex, so it never
        // contains the separator.
        var payload = $"{userId:N}|{clientId}|{(allow ? 1 : 0)}|{expiresAt.ToUnixTimeSeconds()}";
        return _protector.Protect(payload);
    }

    public ConsentDecision? Verify(string? cookieValue, string clientId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieValue)) return null;

        string payload;
        try { payload = _protector.Unprotect(cookieValue); }
        catch { return null; } // tampered, wrong keyring, or not a protected blob

        var parts = payload.Split('|');
        if (parts.Length != 4) return null;
        if (!Guid.TryParseExact(parts[0], "N", out var userId)) return null;
        if (!string.Equals(parts[1], clientId, StringComparison.Ordinal)) return null; // bound to this client
        if (parts[2] is not ("0" or "1")) return null;
        if (!long.TryParse(parts[3], out var expiryUnix)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= now) return null; // expired

        return new ConsentDecision(userId, parts[2] == "1");
    }
}
