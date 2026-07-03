using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IMcpTokenAuthenticator
{
    /// <summary>Verifies a presented MCP token string. Returns the owning user's id on a match (and records
    /// <c>LastUsedAt</c>), or null when the token is blank/unknown.</summary>
    Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct);
}

/// <summary>Verifies incoming MCP bearer tokens by hashing and looking them up. The plaintext is never stored,
/// so this only ever compares hashes. Kept separate from the ASP.NET auth handler so the verification logic is
/// unit-testable without the authentication plumbing.</summary>
public sealed class McpTokenAuthenticator : IMcpTokenAuthenticator
{
    private readonly DiarizDbContext _db;

    public McpTokenAuthenticator(DiarizDbContext db) => _db = db;

    public async Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = McpTokenService.Hash(token.Trim());
        var row = await _db.McpAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return null;

        // Record usage. Only write when it has meaningfully changed to avoid a DB write on every single request.
        var now = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return row.UserId;
    }
}
