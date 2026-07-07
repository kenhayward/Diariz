using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IApiTokenAuthenticator
{
    /// <summary>Verifies a presented API token. Returns the owning user's id when the feature is enabled and
    /// the token matches (recording <c>LastUsedAt</c>), else null (blank/unknown token, or feature disabled).</summary>
    Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct);
}

/// <summary>Verifies incoming API tokens by hashing and looking them up, gated on the platform
/// <see cref="Diariz.Domain.Entities.PlatformSettings.ApiAccessEnabled"/> switch. The plaintext is never
/// stored, so this only ever compares hashes. Kept separate from the ASP.NET auth handler so the verification
/// logic is unit-testable without the authentication plumbing.</summary>
public sealed class ApiTokenAuthenticator : IApiTokenAuthenticator
{
    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;

    public ApiTokenAuthenticator(DiarizDbContext db, IPlatformSettingsService platform)
    {
        _db = db;
        _platform = platform;
    }

    public async Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Platform kill-switch: no key authenticates while the feature is off.
        var settings = await _platform.GetAsync(ct);
        if (!settings.ApiAccessEnabled) return null;

        var hash = ApiTokenService.Hash(token.Trim());
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return null;

        // Record usage. Only write when it has meaningfully changed to avoid a DB write on every request.
        var now = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return row.UserId;
    }
}
