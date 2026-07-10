using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Group storage against real Postgres: the unique name index and both FK cascades, none of which
/// the in-memory provider enforces.</summary>
[Collection(IntegrationCollection.Name)]
public class UserGroupsIntegrationTests(ContainersFixture fx)
{
    private static async Task<Guid> NewUserAsync(DiarizDbContext db)
    {
        var name = $"u{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = name,
            NormalizedEmail = name.ToUpperInvariant(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task GroupName_IsUnique()
    {
        await using var db = fx.CreateDbContext();
        var name = $"Group {Guid.NewGuid():N}";
        db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = name });
        await db.SaveChangesAsync();

        db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = name });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingGroup_CascadesMembers_ButKeepsUsers()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"G {Guid.NewGuid():N}" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        db.UserGroups.Remove(group);
        await db.SaveChangesAsync();

        Assert.Empty(await db.UserGroupMembers.Where(m => m.GroupId == group.Id).ToListAsync());
        Assert.NotNull(await db.Users.FindAsync(userId));
    }

    [Fact]
    public async Task DeletingUser_CascadesTheirMemberships_ButKeepsTheGroup()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"G {Guid.NewGuid():N}" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("user vanished"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.UserGroupMembers.Where(m => m.UserId == userId).ToListAsync());
        Assert.NotNull(await db.UserGroups.FindAsync(group.Id));
    }
}
