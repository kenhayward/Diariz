using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
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
            AccessTokenMinutes = 60
        }));
        return new AuthController(host.Users, tokens);
    }

    [Fact]
    public async Task Register_WithValidCredentials_ReturnsTokenAndPersistsUser()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);

        var result = await controller.Register(new RegisterRequest("new@user.test", GoodPassword));

        var ok = Assert.IsType<OkObjectResult>(result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));

        Assert.NotNull(await host.Users.FindByEmailAsync("new@user.test"));
    }

    [Fact]
    public async Task Register_WithWeakPassword_ReturnsBadRequestAndCreatesNoUser()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);

        var result = await controller.Register(new RegisterRequest("weak@user.test", "short"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsAssignableFrom<IEnumerable<string>>(bad.Value); // Identity error descriptions
        Assert.Null(await host.Users.FindByEmailAsync("weak@user.test"));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsBadRequest()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);
        await controller.Register(new RegisterRequest("dupe@user.test", GoodPassword));

        var result = await controller.Register(new RegisterRequest("dupe@user.test", GoodPassword));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(1, await host.Users.Users.CountAsync(u => u.Email == "dupe@user.test"));
    }

    [Fact]
    public async Task Login_WithCorrectPassword_ReturnsToken()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);
        await controller.Register(new RegisterRequest("login@user.test", GoodPassword));

        var result = await controller.Login(new LoginRequest("login@user.test", GoodPassword));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<AuthResponse>(ok.Value);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);
        await controller.Register(new RegisterRequest("login2@user.test", GoodPassword));

        var result = await controller.Login(new LoginRequest("login2@user.test", "WrongPassword!"));

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host);

        var result = await controller.Login(new LoginRequest("ghost@user.test", GoodPassword));

        Assert.IsType<UnauthorizedResult>(result);
    }
}
