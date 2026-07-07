using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenAuthenticatorTests
{
    private static async Task<(ApiTokenAuthenticator auth, Diariz.Domain.DiarizDbContext db)> BuildAsync(bool enabled)
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, ApiAccessEnabled = enabled });
        await db.SaveChangesAsync();
        return (new ApiTokenAuthenticator(db, new PlatformSettingsService(db)), db);
    }

    private static async Task<string> SeedTokenAsync(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var g = new ApiTokenService().Generate();
        db.ApiAccessTokens.Add(new ApiAccessToken { Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = g.Hash, Prefix = g.Prefix });
        await db.SaveChangesAsync();
        return g.Token;
    }

    [Fact]
    public async Task Authenticate_ReturnsOwner_WhenEnabledAndTokenValid()
    {
        var (auth, db) = await BuildAsync(enabled: true);
        var user = Guid.NewGuid();
        var token = await SeedTokenAsync(db, user);
        Assert.Equal(user, await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_ReturnsNull_WhenFeatureDisabled()
    {
        var (auth, db) = await BuildAsync(enabled: false);
        var token = await SeedTokenAsync(db, Guid.NewGuid());
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_ReturnsNull_ForUnknownOrBlankToken()
    {
        var (auth, _) = await BuildAsync(enabled: true);
        Assert.Null(await auth.AuthenticateAsync("dz_api_nope", default));
        Assert.Null(await auth.AuthenticateAsync("", default));
    }
}
