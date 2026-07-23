using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres checks for the personal API-token credential: the token round-trips through the
/// controller + <see cref="ApiTokenAuthenticator"/>, the platform enable gate is enforced, resolution is
/// owner-scoped, and tokens cascade on user delete. The full HTTP forwarding/isolation matrix (a dz_api_
/// token accepted on /api, a dz_mcp_ token rejected on /api, a dz_api_ token rejected on /mcp, admin parity)
/// is verified live via curl - this project has no WebApplicationFactory harness.</summary>
[Collection(IntegrationCollection.Name)]
public class ApiAccessIntegrationTests(ContainersFixture fx)
{
    private static ApiTokensController Controller(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new ApiTokenService()) { ControllerContext = Http.Context(userId) };

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SetApiAccess(bool enabled)
    {
        await using var db = fx.CreateDbContext();
        var s = await new PlatformSettingsService(db).GetAsync();
        s.ApiAccessEnabled = enabled;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Create_ThenAuthenticate_ResolvesOwner_WhenEnabled_AndStoresOnlyHash()
    {
        var userId = await SeedUser();
        await SetApiAccess(true);
        try
        {
            string token;
            await using (var db = fx.CreateDbContext())
            {
                var created = (await Controller(db, userId).Create(new CreateApiTokenRequest("CI"))).Value!;
                token = created.Token;
                var row = await db.ApiAccessTokens.SingleAsync(t => t.UserId == userId);
                Assert.Equal(ApiTokenService.Hash(token), row.TokenHash);
                Assert.DoesNotContain(token, row.TokenHash);
                Assert.Null(row.LastUsedAt);
            }

            await using (var db = fx.CreateDbContext())
                Assert.Equal(userId, (await new ApiTokenAuthenticator(db, new PlatformSettingsService(db)).AuthenticateAsync(token, default))?.UserId);

            await using (var verify = fx.CreateDbContext())
                Assert.NotNull((await verify.ApiAccessTokens.SingleAsync(t => t.UserId == userId)).LastUsedAt);
        }
        finally { await SetApiAccess(false); }
    }

    [Fact]
    public async Task Authenticate_ReturnsNull_WhenPlatformDisabled()
    {
        var userId = await SeedUser();
        await SetApiAccess(true);
        string token;
        await using (var db = fx.CreateDbContext())
            token = (await Controller(db, userId).Create(new CreateApiTokenRequest("x"))).Value!.Token;

        await SetApiAccess(false); // flip the switch off
        await using (var db = fx.CreateDbContext())
            Assert.Null(await new ApiTokenAuthenticator(db, new PlatformSettingsService(db)).AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_IsOwnerScoped_AcrossUsers()
    {
        var alice = await SeedUser();
        var bob = await SeedUser();
        await SetApiAccess(true);
        try
        {
            string aliceToken;
            await using (var db = fx.CreateDbContext())
                aliceToken = (await Controller(db, alice).Create(new CreateApiTokenRequest("a"))).Value!.Token;
            await using (var db = fx.CreateDbContext())
                await Controller(db, bob).Create(new CreateApiTokenRequest("b"));

            await using (var db = fx.CreateDbContext())
                Assert.Equal(alice, (await new ApiTokenAuthenticator(db, new PlatformSettingsService(db)).AuthenticateAsync(aliceToken, default))?.UserId);
        }
        finally { await SetApiAccess(false); }
    }

    [Fact]
    public async Task Create_NormalizesNonUtcExpiresAt_ToUtcInstant()
    {
        // Npgsql rejects a non-zero-offset DateTimeOffset written to a `timestamptz` column - the in-memory
        // provider used by the unit tests doesn't enforce this, so only a real-Postgres test catches it.
        var userId = await SeedUser();
        var expires = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(5));

        ApiTokenCreatedDto created;
        await using (var db = fx.CreateDbContext())
            created = (await Controller(db, userId).Create(
                new CreateApiTokenRequest("tz", ReadOnly: false, ExpiresAt: expires))).Value!;

        Assert.NotNull(created);

        await using var verify = fx.CreateDbContext();
        var row = await verify.ApiAccessTokens.SingleAsync(t => t.UserId == userId && t.Name == "tz");
        Assert.Equal(expires.ToUniversalTime(), row.ExpiresAt);
    }

    [Fact]
    public async Task DeletingUser_CascadesApiTokens()
    {
        var userId = await SeedUser();
        await SetApiAccess(true);
        try
        {
            await using (var db = fx.CreateDbContext())
                await Controller(db, userId).Create(new CreateApiTokenRequest("x"));

            await using (var db = fx.CreateDbContext())
            {
                db.Users.Remove((await db.Users.FindAsync(userId))!);
                await db.SaveChangesAsync();
            }

            await using var verify = fx.CreateDbContext();
            Assert.False(await verify.ApiAccessTokens.AnyAsync(t => t.UserId == userId));
        }
        finally { await SetApiAccess(false); }
    }
}
