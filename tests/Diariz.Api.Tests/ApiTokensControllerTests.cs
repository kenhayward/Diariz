using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class ApiTokensControllerTests
{
    private static ApiTokensController Build(DiarizDbContext db, Guid userId) =>
        new(db, new ApiTokenService()) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_AndStoresOnlyHash()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var res = await Build(db, user).Create(new CreateApiTokenRequest("CI"));

        var dto = Assert.IsType<ApiTokenCreatedDto>(res.Value);
        Assert.StartsWith("dz_api_", dto.Token);
        var row = await db.ApiAccessTokens.SingleAsync();
        Assert.Equal(ApiTokenService.Hash(dto.Token), row.TokenHash);
        Assert.NotEqual(dto.Token, row.TokenHash); // plaintext not stored
    }

    [Fact]
    public async Task List_ReturnsOwnTokens_WithoutSecret()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        await Build(db, user).Create(new CreateApiTokenRequest("one"));
        var list = await Build(db, user).List();
        Assert.Single(list);
        Assert.StartsWith("dz_api_", list[0].Prefix);
    }

    [Fact]
    public async Task Revoke_DeletesOwnToken()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var created = (await Build(db, user).Create(new CreateApiTokenRequest("x"))).Value!;
        Assert.IsType<NoContentResult>(await Build(db, user).Revoke(created.Id));
        Assert.Empty(await db.ApiAccessTokens.ToListAsync());
    }

    [Fact]
    public async Task Revoke_OthersToken_Returns404()
    {
        var db = TestDb.Create();
        var created = (await Build(db, Guid.NewGuid()).Create(new CreateApiTokenRequest("x"))).Value!;
        Assert.IsType<NotFoundResult>(await Build(db, Guid.NewGuid()).Revoke(created.Id));
    }

    [Fact]
    public async Task Create_persists_readonly_scope_and_expiry()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        await db.SaveChangesAsync();

        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var created = await Build(db, userId).Create(new CreateApiTokenRequest("ci", ReadOnly: true, ExpiresAt: expires));

        var row = await db.ApiAccessTokens.SingleAsync();
        Assert.Equal(ApiTokenScope.ReadOnly, row.Scope);
        Assert.Equal(expires, row.ExpiresAt);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task List_reports_scope_and_expiry()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = "h", Prefix = "dz_api_x",
            Scope = ApiTokenScope.ReadOnly, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var list = await Build(db, userId).List();
        Assert.Equal("ReadOnly", list[0].Scope);
        Assert.NotNull(list[0].ExpiresAt);
    }

    [Fact]
    public async Task Create_rejects_past_expiry()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        await db.SaveChangesAsync();

        var result = await Build(db, userId).Create(
            new CreateApiTokenRequest("x", ExpiresAt: DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.ApiAccessTokens.ToListAsync());
    }
}
