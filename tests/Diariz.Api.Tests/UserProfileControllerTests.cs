using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
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
            new UserPermissions(host.Db), new PeopleDirectory(host.Db))
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

    /// <summary>The MCP switch existed in platform settings but was never reported to the user, so the
    /// Preferences MCP section rendered its controls whatever an administrator had set - the user only
    /// found out when the server refused the token. It travels with the other two flags now.</summary>
    [Fact]
    public async Task Profile_reports_mcp_access_enabled()
    {
        using var host = new IdentityTestHost();
        host.Db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, McpAccessEnabled = true });
        await host.Db.SaveChangesAsync();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.True(res.Value!.McpAccessEnabled);
    }

    [Fact]
    public async Task Profile_reports_mcp_access_disabled()
    {
        using var host = new IdentityTestHost();
        host.Db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, McpAccessEnabled = false });
        await host.Db.SaveChangesAsync();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.False(res.Value!.McpAccessEnabled);
    }

    // ---- Default transcription language ----
    //
    // Deliberately its own setting rather than reusing NativeLanguage: that is the user's own language,
    // used as the default translation target, and plenty of people record meetings in a language that is
    // not their own. Pinning transcription to it would mis-transcribe them by default.

    [Fact]
    public async Task Update_persists_the_default_transcription_language()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);

        await sut.Update(new UpdateUserProfileRequest(null, null, null, TranscriptionLanguage: "de"));

        var userId = Guid.Parse(sut.ControllerContext.HttpContext.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal("de", (await host.Db.UserSettings.FindAsync(userId))!.TranscriptionLanguage);
    }

    [Fact]
    public async Task Profile_reports_the_default_transcription_language()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);
        await sut.Update(new UpdateUserProfileRequest(null, null, null, TranscriptionLanguage: "fr"));

        var res = await sut.Get();

        Assert.Equal("fr", res.Value!.TranscriptionLanguage);
    }

    [Fact]
    public async Task Profile_reports_no_transcription_language_when_the_user_has_not_set_one()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);

        Assert.Null((await sut.Get()).Value!.TranscriptionLanguage);
    }

    [Fact]
    public async Task Update_rejects_an_unsupported_transcription_language()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);

        var res = await sut.Update(new UpdateUserProfileRequest(null, null, null, TranscriptionLanguage: "cy"));

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Update_clears_the_transcription_language_when_it_is_blank()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);
        await sut.Update(new UpdateUserProfileRequest(null, null, null, TranscriptionLanguage: "de"));

        await sut.Update(new UpdateUserProfileRequest(null, null, null, TranscriptionLanguage: ""));

        Assert.Null((await sut.Get()).Value!.TranscriptionLanguage);
    }
}
