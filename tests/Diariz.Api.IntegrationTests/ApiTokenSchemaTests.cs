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

        var scoped = await db.ApiAccessTokens.SingleAsync(t => t.Name == "scoped" && t.UserId == userId);
        var def = await db.ApiAccessTokens.SingleAsync(t => t.Name == "default" && t.UserId == userId);
        Assert.Equal(ApiTokenScope.ReadOnly, scoped.Scope);
        Assert.NotNull(scoped.ExpiresAt);
        Assert.Equal(ApiTokenScope.ReadWrite, def.Scope);
        Assert.Null(def.ExpiresAt);
    }

    [Fact]
    public async Task Column_default_for_Scope_is_ReadWrite_at_the_SQL_level()
    {
        // The C# property initializer (`= ApiTokenScope.ReadWrite`) only guards EF-issued inserts - it does
        // nothing for the actual Postgres column default. Insert via raw SQL that omits the Scope column
        // entirely, so Postgres itself must supply the value, then read it back through EF to prove the
        // migration's `defaultValue: 1` (not the C# initializer) is what keeps pre-existing/externally
        // inserted rows ReadWrite.
        var userId = await SeedUser();

        await using var db = fx.CreateDbContext();
        var rowId = Guid.NewGuid();
        var tokenHash = Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ApiAccessTokens" ("Id", "UserId", "Name", "TokenHash", "Prefix", "CreatedAt")
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            """,
            rowId, userId, "sql-inserted", tokenHash, "dz_api_ccc", DateTimeOffset.UtcNow);

        var row = await db.ApiAccessTokens.SingleAsync(t => t.Id == rowId);
        Assert.Equal(ApiTokenScope.ReadWrite, row.Scope);
    }
}
