using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Diariz.Api.Services;

/// <summary>Hands out a valid Google access token for a user's stored refresh token (Calendar calls).
/// Access tokens are short-lived and kept only in an in-memory cache — never persisted or returned to the
/// browser. A revoked/expired refresh token is cleared so the UI can prompt the user to reconnect.</summary>
public interface IGoogleTokenProvider
{
    /// <summary>A usable access token for the user, or null when they haven't connected / the refresh failed.</summary>
    Task<string?> GetAccessTokenAsync(Guid userId, CancellationToken ct = default);
}

public class GoogleTokenProvider : IGoogleTokenProvider
{
    private readonly DiarizDbContext _db;
    private readonly IGoogleTokenProtector _protector;
    private readonly IGoogleAuthService _google;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GoogleTokenProvider> _log;

    public GoogleTokenProvider(
        DiarizDbContext db, IGoogleTokenProtector protector, IGoogleAuthService google,
        IMemoryCache cache, ILogger<GoogleTokenProvider> log)
    {
        _db = db;
        _protector = protector;
        _google = google;
        _cache = cache;
        _log = log;
    }

    public async Task<string?> GetAccessTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var key = $"google-access:{userId}";
        if (_cache.TryGetValue(key, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var settings = await _db.UserSettings.FindAsync([userId], ct);
        var refresh = _protector.Unprotect(settings?.GoogleRefreshTokenEncrypted);
        if (refresh is null) return null;

        GoogleTokens tokens;
        try
        {
            tokens = await _google.RefreshAsync(refresh, ct);
        }
        catch (Exception ex)
        {
            // A revoked/expired refresh token can't be recovered — clear it so the UI shows "reconnect needed".
            _log.LogWarning(ex, "Google token refresh failed for user {UserId}; clearing the stored connection.", userId);
            if (settings is not null)
            {
                settings.GoogleRefreshTokenEncrypted = null;
                settings.GoogleCalendarGranted = false;
                await _db.SaveChangesAsync(ct);
            }
            return null;
        }

        if (string.IsNullOrEmpty(tokens.AccessToken)) return null;
        // Cache a little short of the real expiry so a call never races an expiring token.
        _cache.Set(key, tokens.AccessToken, TimeSpan.FromSeconds(Math.Max(30, tokens.ExpiresInSeconds - 60)));
        return tokens.AccessToken;
    }
}
