using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A verified API token: the owning user and the token's capability.</summary>
public sealed record ApiTokenAuth(Guid UserId, ApiTokenScope Scope);

public interface IApiTokenAuthenticator
{
    /// <summary>Verifies a presented API token. Returns the owner + scope when the feature is enabled, the
    /// token matches, and it has not expired; else null.</summary>
    Task<ApiTokenAuth?> AuthenticateAsync(string? token, CancellationToken ct);
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

    public async Task<ApiTokenAuth?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var settings = await _platform.GetAsync(ct);
        if (!settings.ApiAccessEnabled) return null;

        var hash = ApiTokenService.Hash(token.Trim());
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return null;

        if (row.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) return null;

        var now = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return new ApiTokenAuth(row.UserId, row.Scope);
    }
}
