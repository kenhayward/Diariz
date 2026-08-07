using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The single place the three calendar sources are merged. Before this existed the merge lived inline
/// in <c>CalendarController.Events</c>, which meant recording-to-meeting matching (a different controller) only
/// ever saw Google - so a user with only <c>.ics</c> feeds could not be matched at all, and an <c>.ics</c>
/// event's detail fetch 404'd.</summary>
public class CalendarAggregatorTests
{
    private sealed class FakeGoogle : IGoogleCalendarClient
    {
        public IReadOnlyList<CalendarEvent>? Events { get; set; } = new List<CalendarEvent>();
        public CalendarEvent? Event { get; set; }
        public string? RequestedEventId { get; private set; }
        public bool GetEventCalled { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult(Events);

        public Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
        {
            GetEventCalled = true;
            RequestedEventId = eventId;
            return Task.FromResult(Event);
        }

        public Task<IReadOnlyList<CalendarListEntry>?> ListAllCalendarsAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarListEntry>?>(null);
    }

    private sealed class FakeIcs : IIcsCalendarClient
    {
        public IReadOnlyList<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
        public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult(Events);
        public Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default) =>
            Task.FromResult((true, (string?)null));
    }

    private sealed class FakeOutlook : IOutlookCalendarStore
    {
        public IReadOnlyList<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
        public CalendarEvent? Event { get; set; }
        public bool HasSource { get; set; }
        public bool GetEventCalled { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult(Events);

        public Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
        {
            GetEventCalled = true;
            return Task.FromResult(Event);
        }

        public Task<bool> HasEnabledSourceAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(HasSource);
    }

    private static CalendarEvent Ev(string id, string summary, string start) =>
        new(id, summary, DateTimeOffset.Parse(start), DateTimeOffset.Parse(start).AddHours(1), null);

    private static readonly DateTimeOffset Min = DateTimeOffset.Parse("2026-07-01T00:00:00Z");
    private static readonly DateTimeOffset Max = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public async Task ListEvents_MergesAllThreeSources_OrderedByStart()
    {
        var google = new FakeGoogle { Events = [Ev("g", "Google", "2026-07-02T11:00:00Z")] };
        var ics = new FakeIcs { Events = [Ev("ics:1:a", "Feed", "2026-07-02T09:00:00Z")] };
        var outlook = new FakeOutlook { Events = [Ev("outlook:2", "Outlook", "2026-07-02T10:00:00Z")] };
        var agg = new CalendarAggregator(google, ics, outlook, TestDb.Create());

        var got = await agg.ListEventsAsync(Guid.NewGuid(), Min, Max);

        Assert.Equal(["Feed", "Outlook", "Google"], got.Select(e => e.Summary));
    }

    /// <summary>A null from Google means "not connected / token revoked", not "error". The other two sources
    /// are independent, so the calendar must still populate - the behaviour the controller had inline.</summary>
    [Fact]
    public async Task ListEvents_WhenGoogleIsNotConnected_StillReturnsTheOtherSources()
    {
        var google = new FakeGoogle { Events = null };
        var ics = new FakeIcs { Events = [Ev("ics:1:a", "Feed", "2026-07-02T09:00:00Z")] };
        var outlook = new FakeOutlook { Events = [Ev("outlook:2", "Outlook", "2026-07-02T10:00:00Z")] };
        var agg = new CalendarAggregator(google, ics, outlook, TestDb.Create());

        var got = await agg.ListEventsAsync(Guid.NewGuid(), Min, Max);

        Assert.Equal(["Feed", "Outlook"], got.Select(e => e.Summary));
    }

    [Fact]
    public async Task ListEvents_WithNothingConnected_IsEmptyRatherThanAnError()
    {
        var agg = new CalendarAggregator(new FakeGoogle { Events = null }, new FakeIcs(), new FakeOutlook(), TestDb.Create());
        Assert.Empty(await agg.ListEventsAsync(Guid.NewGuid(), Min, Max));
    }

    // ---- GetEvent routes by the id's scheme ----

    [Fact]
    public async Task GetEvent_RoutesAnOutlookIdToTheStore_WithoutTouchingGoogle()
    {
        var google = new FakeGoogle();
        var outlook = new FakeOutlook { Event = Ev("outlook:2", "Outlook", "2026-07-02T10:00:00Z") };
        var agg = new CalendarAggregator(google, new FakeIcs(), outlook, TestDb.Create());

        var got = await agg.GetEventAsync(Guid.NewGuid(), "outlook:22222222-2222-2222-2222-222222222222");

        Assert.Equal("Outlook", got!.Summary);
        Assert.True(outlook.GetEventCalled);
        Assert.False(google.GetEventCalled);
    }

    /// <summary>Fixes a real gap: <c>CalendarController.Event</c> only ever asked Google, so opening an
    /// <c>.ics</c> event's details 404'd. The ICS client has no by-id fetch (feeds are parsed wholesale), so
    /// the aggregator resolves one out of the feed listing instead.</summary>
    [Fact]
    public async Task GetEvent_ResolvesAnIcsIdFromTheFeedListing()
    {
        var google = new FakeGoogle();
        var ics = new FakeIcs { Events = [Ev("ics:1:a", "Feed", "2026-07-02T09:00:00Z")] };
        var agg = new CalendarAggregator(google, ics, new FakeOutlook(), TestDb.Create());

        var got = await agg.GetEventAsync(Guid.NewGuid(), "ics:1:a");

        Assert.Equal("Feed", got!.Summary);
        Assert.False(google.GetEventCalled);
    }

    [Fact]
    public async Task GetEvent_RoutesAnythingElseToGoogle()
    {
        var google = new FakeGoogle { Event = Ev("g", "Google", "2026-07-02T11:00:00Z") };
        var outlook = new FakeOutlook();
        var agg = new CalendarAggregator(google, new FakeIcs(), outlook, TestDb.Create());

        var got = await agg.GetEventAsync(Guid.NewGuid(), "some-google-id");

        Assert.Equal("Google", got!.Summary);
        Assert.Equal("some-google-id", google.RequestedEventId);
        Assert.False(outlook.GetEventCalled);
    }

    [Fact]
    public async Task GetEvent_ReturnsNull_WhenTheIdIsUnknown()
    {
        var agg = new CalendarAggregator(new FakeGoogle(), new FakeIcs(), new FakeOutlook(), TestDb.Create());
        Assert.Null(await agg.GetEventAsync(Guid.NewGuid(), "ics:1:missing"));
        Assert.Null(await agg.GetEventAsync(Guid.NewGuid(), "outlook:22222222-2222-2222-2222-222222222222"));
    }

    // ---- HasAnySource ----
    //
    // Answered from the database, not by probing the clients: "do you have a calendar at all" gates the
    // matching endpoint, and it would be absurd for it to cost a Google round trip and every .ics fetch.

    /// <summary>Each source counts on its own. This replaces the <c>GoogleCalendarGranted</c> gate on
    /// recording matching, which is why a feeds-only or Outlook-only user was locked out of it.</summary>
    [Fact]
    public async Task HasAnySource_IsTrueForAConnectedGoogleAccount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.UserSettings.Add(new UserSettings { UserId = userId, GoogleCalendarGranted = true });
        await db.SaveChangesAsync();

        Assert.True(await Agg(db).HasAnySourceAsync(userId));
    }

    [Fact]
    public async Task HasAnySource_IsTrueForAnEnabledIcsFeedAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.IcsCalendarSources.Add(new IcsCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "Team", Url = "https://x.test/f.ics", Enabled = true,
        });
        await db.SaveChangesAsync();

        Assert.True(await Agg(db).HasAnySourceAsync(userId));
    }

    [Fact]
    public async Task HasAnySource_IsTrueForAnEnabledOutlookDeviceAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.OutlookCalendarSources.Add(new OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, DeviceId = "dev-1", DisplayName = "Outlook", Enabled = true,
        });
        await db.SaveChangesAsync();

        Assert.True(await Agg(db).HasAnySourceAsync(userId));
    }

    /// <summary>A source that is kept but switched off does not count - it contributes no events, so offering
    /// to match against it would only produce "no meeting found".</summary>
    [Fact]
    public async Task HasAnySource_IgnoresDisabledSources()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.IcsCalendarSources.Add(new IcsCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "Team", Url = "https://x.test/f.ics", Enabled = false,
        });
        db.OutlookCalendarSources.Add(new OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, DeviceId = "dev-1", DisplayName = "Outlook", Enabled = false,
        });
        await db.SaveChangesAsync();

        Assert.False(await Agg(db).HasAnySourceAsync(userId));
    }

    [Fact]
    public async Task HasAnySource_IsFalseWhenNothingIsConnected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        await db.SaveChangesAsync();

        Assert.False(await Agg(db).HasAnySourceAsync(userId));
    }

    [Fact]
    public async Task HasAnySource_IsScopedToTheCaller()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        Users.Ensure(db, owner);
        db.OutlookCalendarSources.Add(new OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = owner, DeviceId = "dev-1", DisplayName = "Outlook", Enabled = true,
        });
        await db.SaveChangesAsync();

        Assert.False(await Agg(db).HasAnySourceAsync(Guid.NewGuid()));
    }

    private static CalendarAggregator Agg(Diariz.Domain.DiarizDbContext db) =>
        new(new FakeGoogle(), new FakeIcs(), new FakeOutlook(), db);
}
