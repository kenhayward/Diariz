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

    /// <summary>The cookie is only ever read by <c>/connect/authorize</c>, so it is only ever sent there.</summary>
    public const string CookiePath = "/connect";

    /// <summary>The longest a ticket may be valid for. A ticket carrying an expiry further out than this from the
    /// moment it is checked is rejected outright, so a call-site bug cannot mint a long-lived consent.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;

    public OAuthConsentTicketProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Mcp.OAuthConsent.v1");

    public string Issue(Guid userId, string clientId, bool allow, DateTimeOffset expiresAt)
    {
        // userId | clientId | allow(0/1) | expiryUnixSeconds. Registered client ids are our own GUID-hex, but that is
        // checked rather than assumed: the separator must never appear inside a field.
        if (string.IsNullOrEmpty(clientId) || clientId.Contains('|'))
            throw new ArgumentException("A consent ticket needs a client id without the '|' separator.", nameof(clientId));
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
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
        if (expiry <= now) return null;                 // expired
        if (expiry > now.Add(MaxLifetime)) return null; // claims a longer life than any consent is given

        return new ConsentDecision(userId, parts[2] == "1");
    }
}
