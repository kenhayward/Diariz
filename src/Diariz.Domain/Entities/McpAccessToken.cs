namespace Diariz.Domain.Entities;

/// <summary>A personal access token that lets an MCP client (e.g. Claude) connect to the owner's own
/// transcripts over the <c>/mcp</c> endpoint. The secret itself is never stored — only its SHA-256 hash —
/// so a token can be verified on each request but never read back (it is shown to the user exactly once, at
/// generation). A user may hold several named tokens; revoking one simply deletes the row.</summary>
public class McpAccessToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>User-supplied label for the token, e.g. "Claude Desktop". Helps the owner tell tokens apart.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (lowercase, 64 chars) of the full token string. Unique — incoming tokens are
    /// hashed and looked up by this value. The plaintext token is never persisted.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>A short, non-secret display prefix of the token (e.g. <c>dz_mcp_ab12cd</c>) so the owner can
    /// recognise which token a row is in the settings list without exposing the secret.</summary>
    public string Prefix { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the token was last presented on an MCP request. Null until first use.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
