using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Configuration;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The web reads the caller's platform permissions from their profile, not from the JWT: a claim would
/// keep working until token expiry after the user is removed from a group.</summary>
public class UserProfilePermissionsTests
{
    private static TokenService Tokens() => new(Options.Create(new JwtOptions
    {
        Key = "unit-test-signing-key-at-least-32-bytes!!", AccessTokenMinutes = 60,
    }));

    private static async Task<(UserProfileController Sut, Guid UserId)> BuildAsync(
        IdentityTestHost host, PlatformPermission perms)
    {
        var user = new ApplicationUser { UserName = "a@b.test", Email = "a@b.test", IsEnabled = true };
        await host.Users.CreateAsync(user);
        Perms.Grant(host.Db, user.Id, perms);

        var sut = new UserProfileController(
            host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db))
        {
            ControllerContext = Http.Context(user.Id),
        };
        return (sut, user.Id);
    }

    [Fact]
    public async Task Profile_ReportsTheCallersPermissions()
    {
        using var host = new IdentityTestHost();
        var (sut, _) = await BuildAsync(host, PlatformPermission.ManageUsers);

        var profile = (await sut.Get()).Value!;

        Assert.NotNull(profile.Permissions);
        Assert.True(profile.Permissions!.ManageUsers);
        Assert.False(profile.Permissions!.ManagePlatform);
        Assert.False(profile.Permissions!.ManageRooms);
        Assert.False(profile.Permissions!.ManageFormulas);
    }

    [Fact]
    public async Task Profile_ReportsNoPermissions_ForAStandardUser()
    {
        using var host = new IdentityTestHost();
        var (sut, _) = await BuildAsync(host, PlatformPermission.None);

        var profile = (await sut.Get()).Value!;

        Assert.NotNull(profile.Permissions);
        Assert.False(profile.Permissions!.ManageUsers);
        Assert.False(profile.Permissions!.ManagePlatform);
        Assert.False(profile.Permissions!.ManageRooms);
        Assert.False(profile.Permissions!.ManageFormulas);
    }

    [Fact]
    public async Task Profile_ReportsAllThree_ForAPlatformAdministrator()
    {
        using var host = new IdentityTestHost();
        var (sut, _) = await BuildAsync(host, Perms.PlatformAdministrator);

        var profile = (await sut.Get()).Value!;

        Assert.NotNull(profile.Permissions);
        Assert.True(profile.Permissions!.ManageRooms);
        Assert.True(profile.Permissions!.ManageUsers);
        Assert.True(profile.Permissions!.ManagePlatform);
        // The old PlatformAdministrator role bundle predates Formulas and doesn't carry it.
        Assert.False(profile.Permissions!.ManageFormulas);
    }

    [Fact]
    public async Task Profile_ReportsManageFormulasTrue_ForAUserInAGroupWithIt()
    {
        using var host = new IdentityTestHost();
        var (sut, _) = await BuildAsync(host, PlatformPermission.ManageFormulas);

        var profile = (await sut.Get()).Value!;

        Assert.NotNull(profile.Permissions);
        Assert.True(profile.Permissions!.ManageFormulas);
        Assert.False(profile.Permissions!.ManageRooms);
        Assert.False(profile.Permissions!.ManageUsers);
        Assert.False(profile.Permissions!.ManagePlatform);
    }

    [Fact]
    public async Task Profile_ReportsManageFormulasFalse_ForAUserWithoutIt()
    {
        using var host = new IdentityTestHost();
        var (sut, _) = await BuildAsync(host, PlatformPermission.ManageUsers);

        var profile = (await sut.Get()).Value!;

        Assert.NotNull(profile.Permissions);
        Assert.False(profile.Permissions!.ManageFormulas);
    }
}
