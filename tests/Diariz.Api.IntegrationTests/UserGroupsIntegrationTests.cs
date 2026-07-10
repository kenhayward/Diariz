using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
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

    [Fact]
    public async Task SeedGroups_CreatesBothGroups_AndIsIdempotent()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        await Seeder.SeedGroupsAsync(db); // twice: must not duplicate

        var platform = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        var admins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);

        Assert.True(platform.IsSystem);
        Assert.Equal(
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform,
            platform.Permissions);

        Assert.False(admins.IsSystem);
        Assert.Equal(PlatformPermission.ManageRooms | PlatformPermission.ManageUsers, admins.Permissions);
        Assert.False(admins.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }

    /// <summary>The privilege boundary this whole phase must preserve: an Administrator lands in the
    /// Administrators group, NOT in Platform Administrators, so they never gain backup/restore.</summary>
    [Fact]
    public async Task MigrateRoles_MovesAdministratorToAdministrators_WithoutManagePlatform()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        await EnsureRoleAsync(db, Roles.Administrator);

        var adminRoleId = (await db.Roles.SingleAsync(r => r.Name == Roles.Administrator)).Id;
        var userId = await NewUserAsync(db);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = adminRoleId });
        await db.SaveChangesAsync();

        await Seeder.MigrateRolesToGroupsAsync(db);

        var perms = await new UserPermissions(db).ForAsync(userId);
        Assert.True(perms.HasFlag(PlatformPermission.ManageUsers));
        Assert.True(perms.HasFlag(PlatformPermission.ManageRooms));
        Assert.False(perms.HasFlag(PlatformPermission.ManagePlatform));
    }

    [Fact]
    public async Task MigrateRoles_MovesPlatformAdministrator_AndIsIdempotent()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        await EnsureRoleAsync(db, Roles.PlatformAdministrator);

        var roleId = (await db.Roles.SingleAsync(r => r.Name == Roles.PlatformAdministrator)).Id;
        var userId = await NewUserAsync(db);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();

        await Seeder.MigrateRolesToGroupsAsync(db);
        await Seeder.MigrateRolesToGroupsAsync(db); // twice: must not duplicate the membership

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.Single(await db.UserGroupMembers.Where(m => m.GroupId == group.Id && m.UserId == userId).ToListAsync());
        Assert.True((await new UserPermissions(db).ForAsync(userId)).HasFlag(PlatformPermission.ManagePlatform));
    }

    private static async Task EnsureRoleAsync(DiarizDbContext db, string name)
    {
        if (await db.Roles.AnyAsync(r => r.Name == name)) return;
        db.Roles.Add(new IdentityRole<Guid>(name) { Id = Guid.NewGuid(), NormalizedName = name.ToUpperInvariant() });
        await db.SaveChangesAsync();
    }
}
