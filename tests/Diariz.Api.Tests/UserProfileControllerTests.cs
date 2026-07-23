using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Configuration;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>A non-admin SPA can't read /api/platform/settings; it learns feature flags like
/// <c>WebhooksEnabled</c> from the profile instead, exactly as <c>ApiAccessEnabled</c> is exposed today.</summary>
public class UserProfileControllerTests
{
    private static TokenService Tokens() => new(Options.Create(new JwtOptions
    {
        Key = "unit-test-signing-key-at-least-32-bytes!!", AccessTokenMinutes = 60,
    }));

    private static async Task<UserProfileController> BuildAsync(IdentityTestHost host)
    {
        var user = new ApplicationUser { UserName = "a@b.test", Email = "a@b.test", IsEnabled = true };
        await host.Users.CreateAsync(user);

        return new UserProfileController(
            host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db))
        {
            ControllerContext = Http.Context(user.Id),
        };
    }

    [Fact]
    public async Task Profile_reports_webhooks_enabled()
    {
        using var host = new IdentityTestHost();
        host.Db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = true });
        await host.Db.SaveChangesAsync();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.True(res.Value!.WebhooksEnabled);
    }

    [Fact]
    public async Task Profile_reports_webhooks_disabled()
    {
        using var host = new IdentityTestHost();
        host.Db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        await host.Db.SaveChangesAsync();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.False(res.Value!.WebhooksEnabled);
    }
}
