using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class McpTokenAuthenticatorTests
{
    private static async Task<(Guid userId, string token)> SeedToken(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        var g = new McpTokenService().Generate();
        db.McpAccessTokens.Add(new McpAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "test",
            TokenHash = g.Hash,
            Prefix = g.Prefix,
        });
        await db.SaveChangesAsync();
        return (userId, g.Token);
    }

    [Fact]
    public async Task Authenticate_ValidToken_ReturnsUser_AndBumpsLastUsed()
    {
        using var db = TestDb.Create();
        var (userId, token) = await SeedToken(db);
        var auth = new McpTokenAuthenticator(db);

        var result = await auth.AuthenticateAsync(token, default);

        Assert.Equal(userId, result);
        var row = await db.McpAccessTokens.SingleAsync();
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task Authenticate_UnknownToken_ReturnsNull()
    {
        using var db = TestDb.Create();
        await SeedToken(db);
        var auth = new McpTokenAuthenticator(db);

        Assert.Null(await auth.AuthenticateAsync("dz_mcp_not-a-real-token", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Authenticate_BlankToken_ReturnsNull(string? token)
    {
        using var db = TestDb.Create();
        var auth = new McpTokenAuthenticator(db);
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_IsScopedToTheMatchingToken()
    {
        using var db = TestDb.Create();
        var (aliceId, aliceToken) = await SeedToken(db);
        await SeedToken(db); // a second, unrelated token

        var auth = new McpTokenAuthenticator(db);
        Assert.Equal(aliceId, await auth.AuthenticateAsync(aliceToken, default));
    }
}
