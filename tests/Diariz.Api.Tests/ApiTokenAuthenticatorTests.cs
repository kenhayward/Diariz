using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenAuthenticatorTests
{
    private static (DiarizDbContext db, Guid userId, string token) Seed(
        ApiTokenScope scope, DateTimeOffset? expiresAt, bool apiEnabled = true,
        bool ownerEnabled = true, UserStatus ownerStatus = UserStatus.Active)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId, Email = "u@e.com", UserName = "u@e.com", IsEnabled = ownerEnabled, Status = ownerStatus,
        });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, ApiAccessEnabled = apiEnabled });
        var token = "dz_api_" + Guid.NewGuid().ToString("N");
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = ApiTokenService.Hash(token),
            Prefix = ApiTokenService.DisplayPrefix(token), Scope = scope, ExpiresAt = expiresAt,
        });
        db.SaveChanges();
        return (db, userId, token);
    }

    [Fact]
    public async Task Valid_token_returns_user_and_scope()
    {
        var (db, userId, token) = Seed(ApiTokenScope.ReadOnly, expiresAt: null);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        var result = await auth.AuthenticateAsync(token, default);
        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal(ApiTokenScope.ReadOnly, result.Scope);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Feature_disabled_rejects_even_valid_token()
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, apiEnabled: false);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_token_is_rejected(string? blankToken)
    {
        var (db, _, _) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, apiEnabled: true);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(blankToken, default));
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        var (db, _, _) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, apiEnabled: true);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync("dz_api_" + Guid.NewGuid().ToString("N"), default));
    }
    [Fact]
    public async Task A_disabled_owner_is_rejected()
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, ownerEnabled: false);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Theory]
    [InlineData(UserStatus.Requested)]
    [InlineData(UserStatus.Invited)]
    public async Task An_owner_who_is_not_active_is_rejected(UserStatus status)
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, ownerStatus: status);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task A_token_whose_owner_no_longer_exists_is_rejected()
    {
        var (db, userId, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: null);
        db.Users.Remove(db.Users.Single(u => u.Id == userId));
        await db.SaveChangesAsync();
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }
}
