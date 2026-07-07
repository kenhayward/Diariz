using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
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
}
