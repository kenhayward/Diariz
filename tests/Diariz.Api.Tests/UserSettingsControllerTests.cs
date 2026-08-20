using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class UserSettingsControllerTests
{
    private static UserSettingsController Build(
        DiarizDbContext db, Guid userId, SummarizationOptions? server = null, ChatOptions? chat = null,
        IEnumerable<Diariz.Api.Tools.IChatTool>? tools = null, DictationOptions? dictation = null)
    {
        var chatOpts = chat ?? new ChatOptions();
        var registry = new Diariz.Api.Tools.ChatToolRegistry(tools ?? []);
        var toolResolver = new ChatToolSettingsResolver(db, registry, Options.Create(chatOpts));
        return new(db, Options.Create(chatOpts), toolResolver,
            new ChatContextResolver(db, Options.Create(chatOpts), new ChatModelCatalog(db)),
            new LlmSettingsResolver(db, Options.Create(new LlmDefaultsOptions()),
                Options.Create(server ?? new SummarizationOptions()), new FakeApiKeyProtector(), new ChatModelCatalog(db)),
            Options.Create(dictation ?? new DictationOptions()))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    [Fact]
    public async Task Get_NoSettings_ReturnsTheDefaults()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid()).Get();

        Assert.False(dto.ToolsEnabled);
        // The window is the serving model's, and with no model configured that is the server option.
        Assert.Equal(new ChatOptions().ContextLength, dto.ContextWindow);
    }

    [Fact]
    public async Task Settings_AreScopedPerUser()
    {
        using var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await Build(db, alice).Update(new UpdateUserSettingsRequest(ToolsEnabled: true));

        Assert.False((await Build(db, bob).Get()).ToolsEnabled);
    }

    [Fact]
    public async Task Get_NoSettings_DefaultsPlacementToSelectedFolder()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid()).Get();
        Assert.Equal(Diariz.Domain.Entities.RecordingPlacementMode.SelectedFolder, dto.PlacementMode);
        Assert.Null(dto.PlacementSectionId);
    }

    [Fact]
    public async Task Put_SpecificFolder_PersistsTheChosenFolder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            PlacementMode: Diariz.Domain.Entities.RecordingPlacementMode.SpecificFolder, PlacementSectionId: sectionId));

        var dto = await Build(db, userId).Get();
        Assert.Equal(Diariz.Domain.Entities.RecordingPlacementMode.SpecificFolder, dto.PlacementMode);
        Assert.Equal(sectionId, dto.PlacementSectionId);
    }

    [Fact]
    public async Task Put_NonSpecificMode_ClearsAnyFixedFolder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            PlacementMode: Diariz.Domain.Entities.RecordingPlacementMode.SpecificFolder, PlacementSectionId: sectionId));

        // Flipping to SelectedFolder must drop the stale fixed folder.
        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            PlacementMode: Diariz.Domain.Entities.RecordingPlacementMode.SelectedFolder, PlacementSectionId: sectionId));

        var dto = await Build(db, userId).Get();
        Assert.Equal(Diariz.Domain.Entities.RecordingPlacementMode.SelectedFolder, dto.PlacementMode);
        Assert.Null(dto.PlacementSectionId);
    }

    [Fact]
    public async Task Get_ReportsDictationAvailable_WhenSttEndpointConfigured()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid(), dictation: new DictationOptions { ApiBase = "http://stt.test/v1" }).Get();

        Assert.True(dto.DictationServerAvailable);
    }

    [Fact]
    public async Task Get_ReportsDictationUnavailable_WhenNoSttEndpoint()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid()).Get(); // default DictationOptions has an empty ApiBase

        Assert.False(dto.DictationServerAvailable);
    }

    // ---- Desktop Outlook sync opt-in ----

    [Fact]
    public async Task Get_ReportsTheOutlookOptIn_OffByDefault()
    {
        using var db = TestDb.Create();
        Assert.False((await Build(db, Guid.NewGuid()).Get()).OutlookSyncEnabled);
    }

    [Fact]
    public async Task Update_TurnsTheOutlookOptInOn()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        await db.SaveChangesAsync();

        await Build(db, userId).Update(new UpdateUserSettingsRequest( OutlookSyncEnabled: true));

        Assert.True((await db.UserSettings.SingleAsync(s => s.UserId == userId)).OutlookSyncEnabled);
    }

    /// <summary>Omitting the field leaves it alone - the personal settings tabs each PUT only what they own,
    /// so a save from the Model Settings tab must not silently revoke an Outlook opt-in.</summary>
    [Fact]
    public async Task Update_WithoutTheField_LeavesTheOptInAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.UserSettings.Add(new Domain.Entities.UserSettings { UserId = userId, OutlookSyncEnabled = true });
        await db.SaveChangesAsync();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(ToolsEnabled: true));

        Assert.True((await db.UserSettings.SingleAsync(s => s.UserId == userId)).OutlookSyncEnabled);
    }

    /// <summary>Turning the switch off <b>erases</b>, rather than merely stopping future syncs. A privacy
    /// control that leaves the meeting bodies and attendee addresses it gathered sitting on the server is not
    /// one, and this is the behaviour the confirm dialog promises.</summary>
    [Fact]
    public async Task Update_TurningTheOptInOff_PurgesEveryDeviceAndItsEvents()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.UserSettings.Add(new Domain.Entities.UserSettings { UserId = userId, OutlookSyncEnabled = true });
        var source = new Domain.Entities.OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, DeviceId = "dev-1", DisplayName = "Outlook (WORK-PC)",
        };
        db.OutlookCalendarSources.Add(source);
        db.OutlookCalendarEvents.Add(new Domain.Entities.OutlookCalendarEvent
        {
            Id = OutlookEventId.For(source.Id, "uid-1"), SourceId = source.Id, UserId = userId, Uid = "uid-1",
            Subject = "Planning", StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        await Build(db, userId).Update(new UpdateUserSettingsRequest( OutlookSyncEnabled: false));

        Assert.False((await db.UserSettings.SingleAsync(s => s.UserId == userId)).OutlookSyncEnabled);
        Assert.Empty(db.OutlookCalendarSources);
        Assert.Empty(db.OutlookCalendarEvents);
    }

    /// <summary>The purge is the caller's own, never anyone else's.</summary>
    [Fact]
    public async Task Update_TurningTheOptInOff_LeavesOtherUsersDevicesAlone()
    {
        using var db = TestDb.Create();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        Users.Ensure(db, mine);
        Users.Ensure(db, theirs);
        db.UserSettings.Add(new Domain.Entities.UserSettings { UserId = mine, OutlookSyncEnabled = true });
        db.OutlookCalendarSources.Add(new Domain.Entities.OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = mine, DeviceId = "dev-mine", DisplayName = "Mine",
        });
        db.OutlookCalendarSources.Add(new Domain.Entities.OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = theirs, DeviceId = "dev-theirs", DisplayName = "Theirs",
        });
        await db.SaveChangesAsync();

        await Build(db, mine).Update(new UpdateUserSettingsRequest( OutlookSyncEnabled: false));

        var left = Assert.Single(db.OutlookCalendarSources);
        Assert.Equal(theirs, left.UserId);
    }

    // ---- Recording from a calendar event ----

    [Fact]
    public async Task Get_NoSettings_ReturnsCalendarRecordingDefaults()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid()).Get();

        Assert.False(dto.CalendarAutoStopEnabled); // off unless the user asks for it
        Assert.Equal(3, dto.CalendarAutoStopAfterMinutes);
        Assert.Equal(30, dto.CalendarSilenceStopSeconds);
    }

    [Fact]
    public async Task Put_CalendarRecordingPreferences_RoundTrip()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            CalendarAutoStopEnabled: true, CalendarAutoStopAfterMinutes: 10,
            CalendarSilenceStopSeconds: 90));

        var dto = await Build(db, userId).Get();
        Assert.True(dto.CalendarAutoStopEnabled);
        Assert.Equal(10, dto.CalendarAutoStopAfterMinutes);
        Assert.Equal(90, dto.CalendarSilenceStopSeconds);
    }

    [Fact]
    public async Task Put_OtherTabsSave_LeavesCalendarRecordingPreferencesUnchanged()
    {
        // Same tri-state contract as every other field: a tab that doesn't own these omits them.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            CalendarAutoStopEnabled: true, CalendarAutoStopAfterMinutes: 7, CalendarSilenceStopSeconds: 45));

        await Build(db, userId).Update(new UpdateUserSettingsRequest(ToolsEnabled: true));

        var dto = await Build(db, userId).Get();
        Assert.True(dto.CalendarAutoStopEnabled);
        Assert.Equal(7, dto.CalendarAutoStopAfterMinutes);
        Assert.Equal(45, dto.CalendarSilenceStopSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Put_NonPositiveDurations_FallBackToTheDefaults(int bad)
    {
        // A zero or negative wait would stop the recording the instant it started, and a zero silence window
        // would stop it before anyone spoke. Clamp to the defaults rather than persisting a trap.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(
            CalendarAutoStopEnabled: true, CalendarAutoStopAfterMinutes: bad,
            CalendarSilenceStopSeconds: bad));

        var dto = await Build(db, userId).Get();
        Assert.Equal(3, dto.CalendarAutoStopAfterMinutes);
        Assert.Equal(30, dto.CalendarSilenceStopSeconds);
    }

    // ---- LLM timeout override ----

    // ---- Auto-merge speaker segments ----

    [Fact]
    public async Task Get_AutoMergeSpeakerSegments_DefaultsToFalse_WhenThereIsNoSettingsRow()
    {
        using var db = TestDb.Create();

        var dto = await Build(db, Guid.NewGuid()).Get();

        Assert.False(dto.AutoMergeSpeakerSegments);
    }

    [Fact]
    public async Task Update_AutoMergeSpeakerSegments_RoundTrips()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(AutoMergeSpeakerSegments: true));

        Assert.True((await Build(db, userId).Get()).AutoMergeSpeakerSegments);
    }

    [Fact]
    public async Task Update_OmittingAutoMergeSpeakerSegments_LeavesItAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest(AutoMergeSpeakerSegments: true));

        // Another preferences tab saving its own fields must not clear this one (the tri-state rule).
        await Build(db, userId).Update(new UpdateUserSettingsRequest(CalendarAutoStopEnabled: true));

        Assert.True((await Build(db, userId).Get()).AutoMergeSpeakerSegments);
    }
}
