using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class AdminUsersControllerTests
{
    private static AdminUsersController Build(IdentityTestHost host, Guid adminId, FakeEmailSender? email = null) =>
        new(host.Users, email ?? new FakeEmailSender(),
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }))
        {
            ControllerContext = Http.Context(adminId, [Roles.Administrator]),
        };

    private static async Task<ApplicationUser> Seed(
        IdentityTestHost host, string email, string role, UserStatus status = UserStatus.Active)
    {
        var user = new ApplicationUser { UserName = email, Email = email, Status = status, IsEnabled = true };
        await host.Users.CreateAsync(user);
        await host.Users.AddToRoleAsync(user, role);
        return user;
    }

    private static int? StatusOf(IActionResult r) => (r as ObjectResult)?.StatusCode;

    // ---- List ----

    [Fact]
    public async Task List_MapsAccountTypeFromRoles()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        await Seed(host, "plat@x.test", Roles.PlatformAdministrator);
        await Seed(host, "std@x.test", Roles.Standard);

        var list = await Build(host, admin.Id).List();

        Assert.Equal(Roles.PlatformAdministrator, list.Single(u => u.Email == "plat@x.test").AccountType);
        Assert.Equal(Roles.Standard, list.Single(u => u.Email == "std@x.test").AccountType);
    }

    // ---- Grant ----

    [Fact]
    public async Task Grant_Requested_InvitesAndReturnsLink_WhenEmailUnconfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);
        var email = new FakeEmailSender { Sent = false };

        var res = await Build(host, admin.Id, email).Grant(pending.Id);

        var grant = Assert.IsType<GrantResultDto>(res.Value);
        Assert.False(grant.Emailed);
        Assert.Contains("/setup?email=", grant.SetupUrl);
        Assert.Equal(UserStatus.Invited, (await host.Users.FindByIdAsync(pending.Id.ToString()))!.Status);
    }

    [Fact]
    public async Task Grant_EmailsAndHidesLink_WhenConfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);
        var email = new FakeEmailSender { Sent = true };

        var grant = (await Build(host, admin.Id, email).Grant(pending.Id)).Value!;

        Assert.True(grant.Emailed);
        Assert.Null(grant.SetupUrl);
        Assert.Single(email.Messages);
    }

    [Fact]
    public async Task Grant_NonRequested_BadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var active = await Seed(host, "act@x.test", Roles.Standard);

        Assert.IsType<BadRequestObjectResult>((await Build(host, admin.Id).Grant(active.Id)).Result);
    }

    [Fact]
    public async Task Deny_DeletesRequestedUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);

        Assert.IsType<NoContentResult>(await Build(host, admin.Id).Deny(pending.Id));
        Assert.Null(await host.Users.FindByIdAsync(pending.Id.ToString()));
    }

    // ---- Role ----

    [Fact]
    public async Task SetRole_PromotesAndDemotes()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.Administrator));
        Assert.Contains(Roles.Administrator, await host.Users.GetRolesAsync(target));

        await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.Standard));
        var roles = await host.Users.GetRolesAsync(target);
        Assert.Contains(Roles.Standard, roles);
        Assert.DoesNotContain(Roles.Administrator, roles);
    }

    [Fact]
    public async Task SetRole_PlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetRole(plat.Id, new SetRoleRequest(Roles.Standard))));
        Assert.Contains(Roles.PlatformAdministrator, await host.Users.GetRolesAsync(plat)); // unchanged
    }

    [Fact]
    public async Task SetRole_Self_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetRole(admin.Id, new SetRoleRequest(Roles.Standard))));
    }

    [Fact]
    public async Task SetRole_InvalidRole_BadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        Assert.IsType<BadRequestObjectResult>(
            await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.PlatformAdministrator)));
    }

    // ---- Enable / disable ----

    [Fact]
    public async Task SetEnabled_DisablesAndEnables()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        await Build(host, admin.Id).SetEnabled(target.Id, new SetEnabledRequest(false));
        Assert.False((await host.Users.FindByIdAsync(target.Id.ToString()))!.IsEnabled);

        await Build(host, admin.Id).SetEnabled(target.Id, new SetEnabledRequest(true));
        Assert.True((await host.Users.FindByIdAsync(target.Id.ToString()))!.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_DisablePlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetEnabled(plat.Id, new SetEnabledRequest(false))));
    }

    [Fact]
    public async Task SetEnabled_DisableSelf_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetEnabled(admin.Id, new SetEnabledRequest(false))));
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_RemovesUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        Assert.IsType<NoContentResult>(await Build(host, admin.Id).Delete(target.Id));
        Assert.Null(await host.Users.FindByIdAsync(target.Id.ToString()));
    }

    [Fact]
    public async Task Delete_PlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).Delete(plat.Id)));
        Assert.NotNull(await host.Users.FindByIdAsync(plat.Id.ToString()));
    }

    [Fact]
    public async Task Delete_Self_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).Delete(admin.Id)));
    }
}
