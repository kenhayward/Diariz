using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The seeded Administrators and Platform Administrators groups must carry
/// <see cref="PlatformPermission.ManageFormulas"/>, mirroring how they already carry ManageUsers.</summary>
public class SeederFormulasPermissionTests
{
    [Fact]
    public async Task SeedGroupsAsync_grants_ManageFormulas_to_administrators_group()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var admins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);
        Assert.True(admins.Permissions.HasFlag(PlatformPermission.ManageFormulas));
    }

    [Fact]
    public async Task SeedGroupsAsync_grants_ManageFormulas_to_platform_administrators_group()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var platformAdmins = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(platformAdmins.Permissions.HasFlag(PlatformPermission.ManageFormulas));
    }
}
