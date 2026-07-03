using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class McpTokensIntegrationTests(ContainersFixture fx)
{
    private static McpTokensController Controller(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new McpTokenService()) { ControllerContext = Http.Context(userId) };

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Create_ThenAuthenticate_ResolvesTheOwner_AndStoresOnlyTheHash()
    {
        var userId = await SeedUser();

        string token;
        await using (var db = fx.CreateDbContext())
        {
            var created = (await Controller(db, userId).Create(new CreateMcpTokenRequest("Claude"))).Value!;
            token = created.Token;
            // Persisted row holds the hash, never the plaintext.
            var row = await db.McpAccessTokens.SingleAsync(t => t.UserId == userId);
            Assert.Equal(McpTokenService.Hash(token), row.TokenHash);
            Assert.DoesNotContain(token, row.TokenHash);
            Assert.Null(row.LastUsedAt);
        }

        await using (var db = fx.CreateDbContext())
        {
            var resolved = await new McpTokenAuthenticator(db).AuthenticateAsync(token, default);
            Assert.Equal(userId, resolved);
        }

        // LastUsedAt is now recorded.
        await using (var verify = fx.CreateDbContext())
            Assert.NotNull((await verify.McpAccessTokens.SingleAsync(t => t.UserId == userId)).LastUsedAt);
    }

    [Fact]
    public async Task TokenHash_IsUnique()
    {
        var userId = await SeedUser();
        // A unique 64-char hex so it can't clash with another test's row in the shared database.
        var shared = McpTokenService.Hash(Guid.NewGuid().ToString());

        await using var db = fx.CreateDbContext();
        db.McpAccessTokens.Add(new McpAccessToken { Id = Guid.NewGuid(), UserId = userId, Name = "one", TokenHash = shared, Prefix = "dz_mcp_a" });
        await db.SaveChangesAsync();

        db.McpAccessTokens.Add(new McpAccessToken { Id = Guid.NewGuid(), UserId = userId, Name = "two", TokenHash = shared, Prefix = "dz_mcp_a" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingUser_CascadesTokens()
    {
        var userId = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await Controller(db, userId).Create(new CreateMcpTokenRequest("x"));

        await using (var db = fx.CreateDbContext())
        {
            var user = await db.Users.FindAsync(userId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.McpAccessTokens.AnyAsync(t => t.UserId == userId));
    }

    [Fact]
    public async Task Authenticate_IsOwnerScoped_AcrossUsers()
    {
        var alice = await SeedUser();
        var bob = await SeedUser();

        string aliceToken;
        await using (var db = fx.CreateDbContext())
            aliceToken = (await Controller(db, alice).Create(new CreateMcpTokenRequest("a"))).Value!.Token;
        await using (var db = fx.CreateDbContext())
            await Controller(db, bob).Create(new CreateMcpTokenRequest("b"));

        await using (var db = fx.CreateDbContext())
            Assert.Equal(alice, await new McpTokenAuthenticator(db).AuthenticateAsync(aliceToken, default));
    }
}
