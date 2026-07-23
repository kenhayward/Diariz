namespace Diariz.Domain.Entities;

/// <summary>A personal access token that lets a user call the Diariz REST API as themselves. Only the
/// SHA-256 hash of the secret is stored (never the plaintext, which is shown once at generation). A user may
/// hold several named tokens; revoking one deletes the row.</summary>
public class ApiAccessToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>User-supplied label for the token, e.g. "CI pipeline". Helps the owner tell tokens apart.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (lowercase, 64 chars) of the full token string. Unique - incoming tokens are
    /// hashed and looked up by this value. The plaintext token is never persisted.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>A short, non-secret display prefix of the token (e.g. <c>dz_api_ab12cd</c>) so the owner can
    /// recognise which token a row is in the settings list without exposing the secret.</summary>
    public string Prefix { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the token was last presented on an API request. Null until first use.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Coarse capability. Defaults to <see cref="ApiTokenScope.ReadWrite"/> so pre-existing tokens
    /// keep full access; the migration sets the column default to 1 for the same reason.</summary>
    public ApiTokenScope Scope { get; set; } = ApiTokenScope.ReadWrite;

    /// <summary>Optional hard expiry. Null = never expires (all pre-existing tokens).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
