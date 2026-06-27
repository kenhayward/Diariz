using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class AuthControllerTests
{
    private const string GoodPassword = "Sup3rSecret!";

    private static AuthController BuildController(IdentityTestHost host)
    {
        var tokens = new TokenService(Options.Create(new JwtOptions
        {
            Key = "test-signing-key-at-least-32-bytes-long!!",
            AccessTokenMinutes = 60,
        }));
        return new AuthController(host.Users, tokens, new PlatformSettingsService(host.Db));
    }

    private static async Task<ApplicationUser> CreateUser(
        IdentityTestHost host, string email, string? password, UserStatus status, bool enabled = true)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, Status = status, IsEnabled = enabled,
            EmailConfirmed = status == UserStatus.Active,
        };
        if (password is null) await host.Users.CreateAsync(user);
        else await host.Users.CreateAsync(user, password);
        return user;
    }

    private static int? StatusOf(IActionResult r) => (r as ObjectResult)?.StatusCode;

    // ---- Login gating ----

    [Fact]
    public async Task Login_ActiveEnabled_ReturnsToken()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "a@x.test", GoodPassword, UserStatus.Active);

        var result = await BuildController(host).Login(new LoginRequest("a@x.test", GoodPassword));

        Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "a@x.test", GoodPassword, UserStatus.Active);

        Assert.IsType<UnauthorizedResult>(await BuildController(host).Login(new LoginRequest("a@x.test", "Wrong1!")));
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        Assert.IsType<UnauthorizedResult>(await BuildController(host).Login(new LoginRequest("ghost@x.test", GoodPassword)));
    }

    [Fact]
    public async Task Login_Requested_Returns403AwaitingApproval()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "req@x.test", null, UserStatus.Requested);

        var result = await BuildController(host).Login(new LoginRequest("req@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("awaiting approval", ((ObjectResult)result).Value!.ToString());
    }

    [Fact]
    public async Task Login_Invited_Returns403FinishSetup()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "inv@x.test", null, UserStatus.Invited);

        var result = await BuildController(host).Login(new LoginRequest("inv@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("finish setting up", ((ObjectResult)result).Value!.ToString());
    }

    [Fact]
    public async Task Login_Disabled_Returns403Disabled()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "dis@x.test", GoodPassword, UserStatus.Active, enabled: false);

        var result = await BuildController(host).Login(new LoginRequest("dis@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("disabled", ((ObjectResult)result).Value!.ToString());
    }

    // ---- Request access ----

    [Fact]
    public async Task RequestAccess_CreatesRequestedStandardUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();

        await BuildController(host).RequestAccess(new RequestAccessRequest("new@x.test"));

        var user = await host.Users.FindByEmailAsync("new@x.test");
        Assert.NotNull(user);
        Assert.Equal(UserStatus.Requested, user!.Status);
        Assert.False(await host.Users.HasPasswordAsync(user)); // no password yet
        Assert.Contains(Roles.Standard, await host.Users.GetRolesAsync(user));
    }

    [Fact]
    public async Task RequestAccess_ExistingEmail_NeutralAndNoDuplicate()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        await CreateUser(host, "dupe@x.test", GoodPassword, UserStatus.Active);

        var result = await BuildController(host).RequestAccess(new RequestAccessRequest("dupe@x.test"));

        Assert.IsType<OkObjectResult>(result); // same neutral 200
        Assert.Equal(1, await host.Users.Users.CountAsync(u => u.Email == "dupe@x.test"));
    }

    // ---- Setup ----

    private static async Task<(ApplicationUser user, string token)> SeedInvited(IdentityTestHost host, string email)
    {
        await host.SeedRolesAsync();
        var user = await CreateUser(host, email, null, UserStatus.Invited);
        await host.Users.AddToRoleAsync(user, Roles.Standard);
        var token = await host.Users.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, AccountSetup.TokenPurpose);
        return (user, token);
    }

    [Fact]
    public async Task Setup_ValidToken_SetsPasswordNameActive_AndReturnsToken()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada Lovelace", GoodPassword));

        Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var user = await host.Users.FindByEmailAsync("set@x.test");
        Assert.Equal(UserStatus.Active, user!.Status);
        Assert.Equal("Ada Lovelace", user.FullName);
        Assert.True(user.EmailConfirmed);
        Assert.True(await host.Users.CheckPasswordAsync(user, GoodPassword));
    }

    [Fact]
    public async Task Setup_BadToken_ReturnsBadRequest_AndNoActivation()
    {
        using var host = new IdentityTestHost();
        await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", "not-a-token", "Ada", GoodPassword));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(UserStatus.Invited, (await host.Users.FindByEmailAsync("set@x.test"))!.Status);
    }

    [Fact]
    public async Task Setup_BlankFullName_ReturnsBadRequest()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "  ", GoodPassword));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Setup_WeakPassword_ReturnsIdentityErrors()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", "alllowercase"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsAssignableFrom<IEnumerable<string>>(bad.Value);
    }

    [Fact]
    public async Task Setup_Token_IsSingleUse()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");
        await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", GoodPassword));

        // Reusing the same link after activation must fail (status no longer Invited + stamp rotated).
        var again = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", GoodPassword));
        Assert.IsType<BadRequestObjectResult>(again);
    }

    [Fact]
    public async Task ValidateSetup_ReflectsTokenValidity()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var good = (await BuildController(host).ValidateSetup("set@x.test", token)).Value!;
        var bad = (await BuildController(host).ValidateSetup("set@x.test", "garbage")).Value!;

        Assert.True(good.Valid);
        Assert.Equal("set@x.test", good.Email);
        Assert.False(bad.Valid);
    }
}
