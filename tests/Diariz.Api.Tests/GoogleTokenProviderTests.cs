using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class GoogleTokenProviderTests
{
    private static (GoogleTokenProvider provider, DiarizDbContext db, IGoogleTokenProtector protector) Build(FakeGoogle google)
    {
        var db = TestDb.Create();
        var protector = new GoogleTokenProtector(new EphemeralDataProtectionProvider());
        var provider = new GoogleTokenProvider(
            db, protector, google, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GoogleTokenProvider>.Instance);
        return (provider, db, protector);
    }

    [Fact]
    public async Task ReturnsNull_WhenNotConnected()
    {
        var (provider, db, _) = Build(new FakeGoogle());
        var uid = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = uid }); // no refresh token
        await db.SaveChangesAsync();

        Assert.Null(await provider.GetAccessTokenAsync(uid));
    }

    [Fact]
    public async Task RefreshesThenCaches_TheAccessToken()
    {
        var google = new FakeGoogle { Tokens = new GoogleTokens("access-1", null, 3600, "scope", null) };
        var (provider, db, protector) = Build(google);
        var uid = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = uid, GoogleRefreshTokenEncrypted = protector.Protect("refresh-x") });
        await db.SaveChangesAsync();

        Assert.Equal("access-1", await provider.GetAccessTokenAsync(uid));
        Assert.Equal("access-1", await provider.GetAccessTokenAsync(uid)); // second call served from cache
        Assert.Equal(1, google.RefreshCalls);
    }

    [Fact]
    public async Task ClearsStoredConnection_WhenRefreshFails()
    {
        var google = new FakeGoogle { Throw = true };
        var (provider, db, protector) = Build(google);
        var uid = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings
        {
            UserId = uid, GoogleRefreshTokenEncrypted = protector.Protect("refresh-x"), GoogleCalendarGranted = true,
        });
        await db.SaveChangesAsync();

        Assert.Null(await provider.GetAccessTokenAsync(uid)); // revoked refresh token → null
        var s = await db.UserSettings.FindAsync(uid);
        Assert.Null(s!.GoogleRefreshTokenEncrypted);
        Assert.False(s.GoogleCalendarGranted);
    }

    private sealed class FakeGoogle : IGoogleAuthService
    {
        public GoogleTokens Tokens { get; set; } = new("access", null, 3600, null, null);
        public bool Throw { get; set; }
        public int RefreshCalls { get; private set; }

        public bool Enabled => true;
        public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, string scope, bool offline) => "";
        public Task<GoogleTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default) => Task.FromResult(Tokens);
        public Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCalls++;
            if (Throw) throw new InvalidOperationException("invalid_grant");
            return Task.FromResult(Tokens);
        }
        public Task<GoogleUserInfo> ValidateIdTokenAsync(string idToken) => throw new NotImplementedException();
        public Task RevokeAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }
}
