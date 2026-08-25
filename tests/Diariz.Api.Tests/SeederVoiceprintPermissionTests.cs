using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>ManageVoiceprints must be grantable, and must land on the platform administrators group only.
///
/// <para>Mirrors <see cref="SeederPeoplePermissionTests"/>, which exists because ManagePeople once shipped
/// defined, enforced and documented but held by nobody. The asymmetry here is deliberate: this permission
/// confers playback of audio from recordings the holder does not own, and the Administrators group has
/// never carried whole-instance data access.</para></summary>
public class SeederVoiceprintPermissionTests
{
    [Fact]
    public async Task SeedGroupsAsync_grants_ManageVoiceprints_to_platform_administrators()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
    }

    [Fact]
    public async Task SeedGroupsAsync_does_not_grant_ManageVoiceprints_to_administrators()
    {
        // Administrators do directory hygiene without cross-owner audio. If this ever flips, it should be a
        // deliberate decision with its own reasoning, not a copy-paste from the ManagePeople line above it.
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);
        Assert.False(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePeople));
    }

    [Fact]
    public async Task SeedGroupsAsync_adds_ManageVoiceprints_to_an_existing_group()
    {
        // An already-deployed platform must pick the flag up on its next boot, which is why this needs no
        // migration.
        using var db = TestDb.Create();
        db.UserGroups.Add(new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = Seeder.PlatformAdminsGroup,
            IsSystem = true,
            Permissions = PlatformPermission.ManageRooms | PlatformPermission.ManageUsers
                | PlatformPermission.ManagePlatform | PlatformPermission.ManageFormulas
                | PlatformPermission.ManagePeople,
        });
        await db.SaveChangesAsync();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }
}
