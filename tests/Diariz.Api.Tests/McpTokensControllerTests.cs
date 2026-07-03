using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class McpTokensControllerTests
{
    private static McpTokensController Build(DiarizDbContext db, Guid userId) =>
        new(db, new McpTokenService()) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_AndStoresOnlyHash()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        var result = await Build(db, userId).Create(new CreateMcpTokenRequest("Claude Desktop"));
        var dto = Assert.IsType<McpTokenCreatedDto>(result.Value);

        Assert.StartsWith("dz_mcp_", dto.Token);
        Assert.Equal("Claude Desktop", dto.Name);
        Assert.StartsWith(dto.Prefix, dto.Token);

        var stored = await db.McpAccessTokens.SingleAsync(t => t.Id == dto.Id);
        Assert.Equal(userId, stored.UserId);
        Assert.Equal(McpTokenService.Hash(dto.Token), stored.TokenHash);
        // The plaintext token must never be persisted.
        Assert.DoesNotContain(dto.Token, stored.TokenHash);
        Assert.NotEqual(dto.Token, stored.Prefix);
    }

    [Fact]
    public async Task Create_DefaultsName_WhenBlank()
    {
        using var db = TestDb.Create();
        var result = await Build(db, Guid.NewGuid()).Create(new CreateMcpTokenRequest("   "));
        var dto = Assert.IsType<McpTokenCreatedDto>(result.Value);
        Assert.False(string.IsNullOrWhiteSpace(dto.Name));
    }

    [Fact]
    public async Task List_IsScopedPerUser_NewestFirst_AndHidesTheSecret()
    {
        using var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await Build(db, alice).Create(new CreateMcpTokenRequest("one"));
        await Build(db, alice).Create(new CreateMcpTokenRequest("two"));
        await Build(db, bob).Create(new CreateMcpTokenRequest("bob-token"));

        var list = await Build(db, alice).List();
        Assert.Equal(2, list.Count);
        Assert.All(list, t => Assert.StartsWith("dz_mcp_", t.Prefix));
        // The DTO exposes no full-secret field at all.
        Assert.DoesNotContain("Token", System.Text.Json.JsonSerializer.Serialize(list[0]));
    }

    [Fact]
    public async Task Revoke_DeletesOwnToken()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var created = (await Build(db, userId).Create(new CreateMcpTokenRequest("x"))).Value!;

        var res = await Build(db, userId).Revoke(created.Id);
        Assert.IsType<NoContentResult>(res);
        Assert.Empty(await db.McpAccessTokens.ToListAsync());
    }

    [Fact]
    public async Task Revoke_OtherUsersToken_ReturnsNotFound_AndLeavesItIntact()
    {
        using var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var aliceToken = (await Build(db, alice).Create(new CreateMcpTokenRequest("x"))).Value!;

        var res = await Build(db, bob).Revoke(aliceToken.Id);
        Assert.IsType<NotFoundResult>(res);
        Assert.Single(await db.McpAccessTokens.ToListAsync());
    }
}
