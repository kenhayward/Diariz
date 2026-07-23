using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class ApiTokenSchemaTests(ContainersFixture fx)
{
    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Token_persists_scope_and_expiry_and_defaults_to_readwrite()
    {
        var userId = await SeedUser();

        await using var db = fx.CreateDbContext();
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "scoped", TokenHash = Guid.NewGuid().ToString("N"),
            Prefix = "dz_api_aaa", Scope = ApiTokenScope.ReadOnly, ExpiresAt = expires,
        });
        // A token created without setting Scope must default to ReadWrite (backward compatible).
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "default", TokenHash = Guid.NewGuid().ToString("N"),
            Prefix = "dz_api_bbb",
        });
        await db.SaveChangesAsync();

        var scoped = await db.ApiAccessTokens.SingleAsync(t => t.Name == "scoped");
        var def = await db.ApiAccessTokens.SingleAsync(t => t.Name == "default");
        Assert.Equal(ApiTokenScope.ReadOnly, scoped.Scope);
        Assert.NotNull(scoped.ExpiresAt);
        Assert.Equal(ApiTokenScope.ReadWrite, def.Scope);
        Assert.Null(def.ExpiresAt);
    }
}
