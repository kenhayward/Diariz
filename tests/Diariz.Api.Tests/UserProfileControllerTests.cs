using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Diariz.Api.Services;
using Diariz.Api.Configuration;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The controller for an EXISTING user, when a test needs to seed against that user first.</summary>
    private static UserProfileController Build(IdentityTestHost host, Guid userId) =>
        new(host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db), new PeopleDirectory(host.Db), new RoomScope(host.Db))
        {
            ControllerContext = Http.Context(userId),
        };

    private static async Task<UserProfileController> BuildAsync(IdentityTestHost host)
    {
        var user = new ApplicationUser { UserName = "a@b.test", Email = "a@b.test", IsEnabled = true };
        await host.Users.CreateAsync(user);

        return new UserProfileController(
            host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db), new PeopleDirectory(host.Db), new RoomScope(host.Db))
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

    /// <summary>The invariant: after any rename, the personal room reads the same as the display name. It
    /// used to drift silently - the person was re-synced on save and the room was not, so a production
    /// account sat under the seeded name "Platform Administrator" long after being renamed. This test is the
    /// guard against a fourth FullName write site forgetting to call the sync.</summary>
    [Fact]
    public async Task Renaming_AlsoRenamesThePersonalRoom()
    {
        using var host = new IdentityTestHost();
        var user = new ApplicationUser
        {
            UserName = "rename@b.test", Email = "rename@b.test", IsEnabled = true, FullName = "Old Name",
        };
        await host.Users.CreateAsync(user);
        var rooms = new RoomScope(host.Db);
        var roomId = await rooms.PersonalRoomIdAsync(user.Id);
        Assert.Equal("Old Name", host.Db.Rooms.Single(r => r.Id == roomId).Name);

        var sut = new UserProfileController(
            host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db), new PeopleDirectory(host.Db), rooms)
        {
            ControllerContext = Http.Context(user.Id),
        };

        await sut.Update(new UpdateUserProfileRequest(
            FullName: "New Name", NativeLanguage: null, UiLanguage: null));

        Assert.Equal("New Name", host.Db.Rooms.Single(r => r.Id == roomId).Name);
    }

    /// <summary>Every account is also a Person (Person.LinkedUserId), and that person is what carries the
    /// voiceprint - but the directory that would show it is gated behind ManagePeople, so an ordinary user
    /// had no way to see their own row at all. The profile reports it read-only.</summary>
    [Fact]
    public async Task Profile_reports_the_linked_person_and_its_voiceprint()
    {
        using var host = new IdentityTestHost();
        var user = new ApplicationUser
        {
            UserName = "vp@b.test", Email = "vp@b.test", IsEnabled = true, FullName = "Ken Hayward",
        };
        await host.Users.CreateAsync(user);
        var person = await new PeopleDirectory(host.Db).EnsureForUserAsync(user.Id);
        person.SampleCount = 8;
        await host.Db.SaveChangesAsync();
        var sut = Build(host, user.Id);

        var res = await sut.Get();

        Assert.Equal(person.Id, res.Value!.Person!.Id);
        Assert.Equal("Ken Hayward", res.Value.Person.Name);
        Assert.True(res.Value.Person.HasVoiceprint);
        Assert.Equal(8, res.Value.Person.SampleCount);
        Assert.False(res.Value.Person.VoiceprintOptOut);
    }

    /// <summary>No samples means no voiceprint, which is the ordinary case and the one the UI has to tell
    /// the user about.</summary>
    [Fact]
    public async Task Profile_reports_no_voiceprint_when_there_are_no_samples()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.NotNull(res.Value!.Person);
        Assert.False(res.Value.Person!.HasVoiceprint);
        Assert.Equal(0, res.Value.Person.SampleCount);
    }

    /// <summary>Self-heal, mirroring PeopleController.List: an account created by a path that forgot to
    /// provision still gets a block rather than a blank one.</summary>
    [Fact]
    public async Task Profile_provisions_the_person_when_the_account_has_none()
    {
        using var host = new IdentityTestHost();
        var user = new ApplicationUser { UserName = "np@b.test", Email = "np@b.test", IsEnabled = true };
        await host.Users.CreateAsync(user);
        Assert.Equal(0, await host.Db.People.CountAsync(p => p.LinkedUserId == user.Id));
        var sut = Build(host, user.Id);

        var res = await sut.Get();

        Assert.NotNull(res.Value!.Person);
        Assert.Equal(1, await host.Db.People.CountAsync(p => p.LinkedUserId == user.Id));
    }

    /// <summary>Someone who has opted out has no voiceprint and never will until they opt back in and are
    /// enrolled again, so the block says that rather than "none yet".</summary>
    [Fact]
    public async Task Profile_reports_the_voiceprint_opt_out()
    {
        using var host = new IdentityTestHost();
        var user = new ApplicationUser { UserName = "oo@b.test", Email = "oo@b.test", IsEnabled = true };
        await host.Users.CreateAsync(user);
        var person = await new PeopleDirectory(host.Db).EnsureForUserAsync(user.Id);
        person.VoiceprintOptOut = true;
        await host.Db.SaveChangesAsync();
        var sut = Build(host, user.Id);

        var res = await sut.Get();

        Assert.True(res.Value!.Person!.VoiceprintOptOut);
    }
}
