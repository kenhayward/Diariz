using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class UserProfileIntegrationTests(ContainersFixture fx)
{
    private ServiceProvider BuildIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DiarizDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider();
    }

    private static TokenService Tokens() => new(Options.Create(new JwtOptions
    {
        Key = "integration-signing-key-at-least-32-bytes!!", AccessTokenMinutes = 60,
    }));

    private static UserProfileController Build(ServiceProvider sp, Guid userId)
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<DiarizDbContext>();
        return new UserProfileController(users, db, Tokens())
        {
            ControllerContext = Http.Context(userId),
        };
    }

    [Fact]
    public async Task Update_SetsNameAndLanguages_AndReturnsFreshTokenWithNewName()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = $"p-{Guid.NewGuid():N}@x.test", Email = $"p-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(user);

        var result = await Build(sp, user.Id).Update(new UpdateUserProfileRequest("Renamed Person", "es", "fr"));

        var auth = Assert.IsType<AuthResponse>(result.Value);
        var name = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Claims.First(c => c.Type == "name").Value;
        Assert.Equal("Renamed Person", name); // fresh token carries the new display name

        await using var verify = fx.CreateDbContext();
        var saved = await verify.Users.FindAsync(user.Id);
        Assert.Equal("Renamed Person", saved!.FullName);
        var settings = await verify.UserSettings.FindAsync(user.Id);
        Assert.Equal("es", settings!.NativeLanguage);
        Assert.Equal("fr", settings.UiLanguage);

        // GET reflects what was stored.
        var got = Assert.IsType<UserProfileDto>((await Build(sp, user.Id).Get()).Value);
        Assert.Equal("Renamed Person", got.FullName);
        Assert.Equal("es", got.NativeLanguage);
        Assert.Equal("fr", got.UiLanguage);
    }

    [Fact]
    public async Task Get_ReflectsWhetherGoogleIsLinked()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var linked = new ApplicationUser { UserName = $"g-{Guid.NewGuid():N}@x.test", Email = $"g-{Guid.NewGuid():N}@x.test", GoogleSubject = $"sub-{Guid.NewGuid():N}" };
        var plain = new ApplicationUser { UserName = $"p-{Guid.NewGuid():N}@x.test", Email = $"p-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(linked);
        await users.CreateAsync(plain);

        Assert.True(Assert.IsType<UserProfileDto>((await Build(sp, linked.Id).Get()).Value).GoogleConnected);
        Assert.False(Assert.IsType<UserProfileDto>((await Build(sp, plain.Id).Get()).Value).GoogleConnected);
    }

    [Fact]
    public async Task Update_RejectsUnknownLanguage()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = $"p-{Guid.NewGuid():N}@x.test", Email = $"p-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(user);

        var result = await Build(sp, user.Id).Update(new UpdateUserProfileRequest("X", "klingon", null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_BlankFields_ClearNameAndLanguages()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"p-{Guid.NewGuid():N}@x.test", Email = $"p-{Guid.NewGuid():N}@x.test", FullName = "Old Name",
        };
        await users.CreateAsync(user);
        await Build(sp, user.Id).Update(new UpdateUserProfileRequest("Set Name", "de", "de"));

        await Build(sp, user.Id).Update(new UpdateUserProfileRequest("  ", "", ""));

        await using var verify = fx.CreateDbContext();
        Assert.Null((await verify.Users.FindAsync(user.Id))!.FullName);
        var settings = await verify.UserSettings.FindAsync(user.Id);
        Assert.Null(settings!.NativeLanguage);
        Assert.Null(settings.UiLanguage);
    }
}
