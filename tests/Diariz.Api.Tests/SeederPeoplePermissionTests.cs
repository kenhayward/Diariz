using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The seeded groups must carry <see cref="PlatformPermission.ManagePeople"/>, mirroring
/// <see cref="SeederFormulasPermissionTests"/>.
///
/// <para>This exists because the permission shipped ungrantable: it was defined, enforced on every People
/// endpoint, and documented - but no group was ever given it and the Groups screen had no checkbox for it,
/// so the People page was unreachable on a real deployment. Every other test granted it explicitly through
/// <c>Perms.Grant</c>, which exercises the mechanism while saying nothing about whether an administrator
/// can actually obtain it.</para></summary>
public class SeederPeoplePermissionTests
{
    [Fact]
    public async Task SeedGroupsAsync_grants_ManagePeople_to_administrators_group()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var admins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);
        Assert.True(admins.Permissions.HasFlag(PlatformPermission.ManagePeople));
    }

    [Fact]
    public async Task SeedGroupsAsync_grants_ManagePeople_to_platform_administrators_group()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var platformAdmins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(platformAdmins.Permissions.HasFlag(PlatformPermission.ManagePeople));
    }

    /// <summary>An already-deployed platform must pick the new flag up on the next boot, not only on a fresh
    /// database - that backfill is the whole reason this needs no migration.</summary>
    [Fact]
    public async Task SeedGroupsAsync_adds_ManagePeople_to_an_existing_group()
    {
        using var db = TestDb.Create();
        db.UserGroups.Add(new UserGroup
        {
            Id = Guid.NewGuid(), Name = Seeder.PlatformAdminsGroup, IsSystem = true,
            // What a pre-0.164 deployment holds: rooms, users, platform, formulas - and no people.
            Permissions = PlatformPermission.ManageRooms | PlatformPermission.ManageUsers
                | PlatformPermission.ManagePlatform | PlatformPermission.ManageFormulas,
        });
        await db.SaveChangesAsync();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePeople));
        // and nothing it already had was revoked
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }

    /// <summary>The general guard: **every** platform permission must be reachable, either by being seeded
    /// or by being tickable in Settings, Groups. A future flag that is neither is a feature nobody can turn
    /// on - which is exactly what happened here.
    ///
    /// <para>The web list is mirrored by hand in <c>GroupsTab.tsx</c> (PERMISSION_BITS), so this asserts the
    /// server side and <c>groupsPermissionBits.test.ts</c> asserts the client keeps up.</para></summary>
    [Fact]
    public async Task Every_platform_permission_is_granted_to_a_seeded_group()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var seeded = (await db.UserGroups.ToListAsync())
            .Aggregate(PlatformPermission.None, (all, g) => all | g.Permissions);

        var ungrantable = Enum.GetValues<PlatformPermission>()
            .Where(p => p != PlatformPermission.None && !seeded.HasFlag(p))
            .ToList();

        Assert.Empty(ungrantable);
    }
}
