using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
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
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform
                | PlatformPermission.ManageFormulas,
            platform.Permissions);

        Assert.False(admins.IsSystem);
        Assert.Equal(
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManageFormulas,
            admins.Permissions);
        Assert.False(admins.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }

    /// <summary>The privilege boundary this whole phase must preserve: an Administrator lands in the
    /// Administrators group, NOT in Platform Administrators, so they never gain backup/restore. Exercises the
    /// migration's backfill SQL, which is what actually runs against an upgrading deployment.</summary>
    [Fact]
    public async Task Backfill_MovesAdministratorToAdministrators_WithoutManagePlatform()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        var userId = await UserWithRoleAsync(db, Roles.Administrator);

        await db.Database.ExecuteSqlRawAsync(RoleToGroupBackfill.Sql);

        var perms = await new UserPermissions(db).ForAsync(userId);
        Assert.True(perms.HasFlag(PlatformPermission.ManageUsers));
        Assert.True(perms.HasFlag(PlatformPermission.ManageRooms));
        Assert.False(perms.HasFlag(PlatformPermission.ManagePlatform));
    }

    [Fact]
    public async Task Backfill_MovesPlatformAdministrator_AndDoesNotDuplicate()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        var userId = await UserWithRoleAsync(db, Roles.PlatformAdministrator);

        await db.Database.ExecuteSqlRawAsync(RoleToGroupBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(RoleToGroupBackfill.Sql); // ON CONFLICT DO NOTHING

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.Single(await db.UserGroupMembers.Where(m => m.GroupId == group.Id && m.UserId == userId).ToListAsync());
        Assert.True((await new UserPermissions(db).ForAsync(userId)).HasFlag(PlatformPermission.ManagePlatform));
    }

    /// <summary>Regression: demoting an Administrator must STICK across a restart. The backfill is a one-way
    /// move that runs once per database (inside the migration). If it were re-run on every boot, it would see
    /// the still-present legacy AspNetUserRoles row and silently re-promote the user.</summary>
    [Fact]
    public async Task Demotion_IsNotUndoneByStartupSeeding()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        var userId = await UserWithRoleAsync(db, Roles.Administrator); // legacy role row, as after an upgrade
        await db.Database.ExecuteSqlRawAsync(RoleToGroupBackfill.Sql);

        // The admin demotes them (AdminUsersController.SetRole removes the group membership).
        var admins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);
        db.UserGroupMembers.Remove(
            await db.UserGroupMembers.SingleAsync(m => m.GroupId == admins.Id && m.UserId == userId));
        await db.SaveChangesAsync();

        // Now restart the server: everything Program.cs runs at boot for platform authority.
        await Seeder.SeedPlatformAuthorityAsync(db, seedUserId: null);

        Assert.Equal(PlatformPermission.None, await new UserPermissions(db).ForAsync(userId));
    }

    [Fact]
    public async Task SeedPlatformAuthority_PutsTheSeedUserInPlatformAdministrators()
    {
        await using var db = fx.CreateDbContext();
        var seedUserId = await NewUserAsync(db);

        await Seeder.SeedPlatformAuthorityAsync(db, seedUserId);
        await Seeder.SeedPlatformAuthorityAsync(db, seedUserId); // idempotent

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.Single(await db.UserGroupMembers.Where(m => m.GroupId == group.Id && m.UserId == seedUserId).ToListAsync());
        Assert.True((await new UserPermissions(db).ForAsync(seedUserId)).HasFlag(PlatformPermission.ManagePlatform));
    }

    private static async Task<Guid> UserWithRoleAsync(DiarizDbContext db, string roleName)
    {
        if (!await db.Roles.AnyAsync(r => r.Name == roleName))
        {
            db.Roles.Add(new IdentityRole<Guid>(roleName)
            {
                Id = Guid.NewGuid(), NormalizedName = roleName.ToUpperInvariant(),
            });
            await db.SaveChangesAsync();
        }
        var roleId = (await db.Roles.SingleAsync(r => r.Name == roleName)).Id;
        var userId = await NewUserAsync(db);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();
        return userId;
    }
}
