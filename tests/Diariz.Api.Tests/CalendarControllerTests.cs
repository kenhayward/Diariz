using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class CalendarControllerTests
{
    private sealed class FakeCalendarClient : IGoogleCalendarClient
    {
        public IReadOnlyList<CalendarEvent>? Events { get; set; } = new List<CalendarEvent>();
        public CalendarEvent? Event { get; set; }
        public string? RequestedEventId { get; private set; }
        public DateTimeOffset? TimeMin { get; private set; }
        public DateTimeOffset? TimeMax { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
        {
            TimeMin = timeMin; TimeMax = timeMax;
            return Task.FromResult(Events);
        }

        public Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
        {
            RequestedEventId = eventId;
            return Task.FromResult(Event);
        }

        public IReadOnlyList<CalendarListEntry>? AllCalendars { get; set; }
        public Task<IReadOnlyList<CalendarListEntry>?> ListAllCalendarsAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(AllCalendars);
    }

    private sealed class FakeIcsClient : IIcsCalendarClient
    {
        public IReadOnlyList<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
        public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult(Events);
        public Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default) =>
            Task.FromResult((true, (string?)null));
    }

    /// <summary>Builds the controller over a <b>real</b> <see cref="CalendarAggregator"/> wrapping the fakes, so
    /// these tests still cover the actual merge rather than a fake of it - the merge moved out of the controller
    /// but its behaviour is still this controller's contract.</summary>
    private static CalendarController Build(
        FakeCalendarClient cal, Guid userId, FakeIcsClient? ics = null, IGoogleCalendarSelectionStore? selection = null,
        IOutlookCalendarStore? outlook = null, DiarizDbContext? db = null)
    {
        db ??= TestDb.Create();
        return new CalendarController(
            cal,
            new CalendarAggregator(cal, ics ?? new FakeIcsClient(), outlook ?? new NoOutlookDevices(), TestDb.Create()),
            selection ?? new GoogleCalendarSelectionStore(TestDb.Create()),
            db)
        {
            ControllerContext = Http.Context(userId),
        };
    }

    private static readonly DateTimeOffset Min = DateTimeOffset.Parse("2026-07-01T00:00:00Z");
    private static readonly DateTimeOffset Max = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public async Task Events_ReturnsEventsFromClient_AndForwardsTheWindow()
    {
        var cal = new FakeCalendarClient
        {
            Events = new List<CalendarEvent>
            {
                new("e1", "Standup", Min.AddHours(9), Min.AddHours(9.5), "https://cal/e1"),
            },
        };
        var controller = Build(cal, Guid.NewGuid());

        var result = await controller.Events(Min, Max, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CalendarEvent>>(ok.Value);
        Assert.Equal("e1", Assert.Single(events).Id);
        Assert.Equal(Min, cal.TimeMin);
        Assert.Equal(Max, cal.TimeMax);
    }

    [Fact]
    public async Task Events_WhenNotConnected_ReturnsEmptyList()
    {
        var cal = new FakeCalendarClient { Events = null }; // null = no token / not connected
        var controller = Build(cal, Guid.NewGuid());

        var result = await controller.Events(Min, Max, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CalendarEvent>>(ok.Value));
    }

    [Fact]
    public async Task Events_MergesGoogleAndIcsFeeds_OrderedByStart()
    {
        var cal = new FakeCalendarClient
        {
            Events = new List<CalendarEvent> { new("g1", "Google", Min.AddHours(10), Min.AddHours(11), null) },
        };
        var ics = new FakeIcsClient
        {
            Events = new List<CalendarEvent>
            {
                new("ics:src:e", "Team feed", Min.AddHours(9), Min.AddHours(9.5), null, CalendarId: "ics:src"),
            },
        };
        var controller = Build(cal, Guid.NewGuid(), ics);

        var ok = Assert.IsType<OkObjectResult>((await controller.Events(Min, Max, default)).Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<CalendarEvent>>(ok.Value);
        Assert.Equal(2, events.Count);
        Assert.Equal("Team feed", events[0].Summary); // earlier start first
        Assert.Equal("Google", events[1].Summary);
    }

    [Fact]
    public async Task Events_ReturnsIcsEvents_EvenWhenGoogleNotConnected()
    {
        var cal = new FakeCalendarClient { Events = null }; // Google not connected
        var ics = new FakeIcsClient
        {
            Events = new List<CalendarEvent> { new("ics:src:e", "Team feed", Min.AddHours(9), Min.AddHours(10), null) },
        };
        var controller = Build(cal, Guid.NewGuid(), ics);

        var ok = Assert.IsType<OkObjectResult>((await controller.Events(Min, Max, default)).Result);
        Assert.Equal("Team feed", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CalendarEvent>>(ok.Value)).Summary);
    }

    [Fact]
    public async Task Events_WhenTimeMaxNotAfterMin_ReturnsBadRequest()
    {
        var cal = new FakeCalendarClient();
        var controller = Build(cal, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>((await controller.Events(Max, Min, default)).Result);
        Assert.Null(cal.TimeMin); // never reached the client
    }

    [Fact]
    public async Task Events_WhenRangeTooLarge_ReturnsBadRequest()
    {
        var cal = new FakeCalendarClient();
        var controller = Build(cal, Guid.NewGuid());

        var tooWide = Min.AddDays(90);
        Assert.IsType<BadRequestObjectResult>((await controller.Events(Min, tooWide, default)).Result);
        Assert.Null(cal.TimeMin);
    }

    [Fact]
    public async Task Event_ReturnsTheEvent_ById()
    {
        var cal = new FakeCalendarClient
        {
            Event = new("evt1", "Planning", Min.AddHours(9), Min.AddHours(10), "https://cal/evt1",
                Description: "Agenda", Location: "Room 4"),
        };
        var controller = Build(cal, Guid.NewGuid());

        var ok = Assert.IsType<OkObjectResult>((await controller.Event("evt1", default)).Result);
        Assert.Equal("evt1", Assert.IsType<CalendarEvent>(ok.Value).Id);
        Assert.Equal("evt1", cal.RequestedEventId);
    }

    [Fact]
    public async Task Event_WhenMissingOrNotConnected_ReturnsNotFound()
    {
        var cal = new FakeCalendarClient { Event = null };
        var controller = Build(cal, Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await controller.Event("nope", default)).Result);
    }

    // ---- Calendar selection (Preferences picker) ----

    private static FakeCalendarClient CalsClient() => new()
    {
        AllCalendars = new List<CalendarListEntry>
        {
            new("primary", "Me", "#ff0000", "#fff", Selected: false, Primary: true),
            new("shown", "Work", "#00ff00", "#fff", Selected: true, Primary: false),
            new("hidden", "Old", "#0000ff", "#fff", Selected: false, Primary: false),
        },
    };

    [Fact]
    public async Task Calendars_DefaultsSelectedToGoogleVisiblePlusPrimary_WhenNoneStored()
    {
        var ok = Assert.IsType<OkObjectResult>((await Build(CalsClient(), Guid.NewGuid()).Calendars(default)).Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CalendarListItemDto>>(ok.Value);

        Assert.True(items.Single(i => i.Id == "primary").Selected);
        Assert.True(items.Single(i => i.Id == "shown").Selected);
        Assert.False(items.Single(i => i.Id == "hidden").Selected);
        Assert.Equal("#ff0000", items.Single(i => i.Id == "primary").BackgroundColor); // colour surfaced
    }

    [Fact]
    public async Task Calendars_WhenNotConnected_ReturnsEmpty()
    {
        var cal = new FakeCalendarClient { AllCalendars = null };
        var ok = Assert.IsType<OkObjectResult>((await Build(cal, Guid.NewGuid()).Calendars(default)).Result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CalendarListItemDto>>(ok.Value));
    }

    [Fact]
    public async Task SaveCalendars_ThenCalendars_ReflectsStoredSelection()
    {
        using var db = TestDb.Create();
        var store = new GoogleCalendarSelectionStore(db);
        var userId = Guid.NewGuid();

        await Build(CalsClient(), userId, selection: store)
            .SaveCalendars(new SaveCalendarSelectionRequest(["primary"]), default);

        var ok = Assert.IsType<OkObjectResult>((await Build(CalsClient(), userId, selection: store).Calendars(default)).Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CalendarListItemDto>>(ok.Value);
        Assert.True(items.Single(i => i.Id == "primary").Selected);
        Assert.False(items.Single(i => i.Id == "shown").Selected); // was Google-visible; now excluded by the explicit set
        Assert.False(items.Single(i => i.Id == "hidden").Selected);
    }

    // ---- Series recordings ----

    /// <summary>The other recordings of the same recurring meeting: found by the stored series key, scoped to
    /// the caller, and never including the occurrence being viewed.</summary>
    [Fact]
    public async Task SeriesRecordings_ReturnsTheOwnersOtherRecordingsOfTheSameSeries()
    {
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        var db = TestDb.Create();

        var mine = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Standup 3 Aug" };
        var current = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Standup 10 Aug" };
        var theirs = new Recording { Id = Guid.NewGuid(), UserId = someoneElse, Title = "Their standup" };
        var otherSeries = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Retro" };
        db.AddRange(mine, current, theirs, otherSeries);
        db.AddRange(
            Link(mine.Id, "abc_20260803T090000Z", "abc", "2026-08-03T09:00:00Z"),
            Link(current.Id, "abc_20260810T090000Z", "abc", "2026-08-10T09:00:00Z"),
            Link(theirs.Id, "abc_20260727T090000Z", "abc", "2026-07-27T09:00:00Z"),
            Link(otherSeries.Id, "zzz_20260803T140000Z", "zzz", "2026-08-03T14:00:00Z"));
        await db.SaveChangesAsync();

        var cal = new FakeCalendarClient
        {
            Event = new CalendarEvent("abc_20260810T090000Z", "Standup",
                DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"), null,
                Recurring: true, SeriesId: "abc"),
        };

        var result = await Build(cal, me, db: db).SeriesRecordings("abc_20260810T090000Z", default);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<SeriesRecordingDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var only = Assert.Single(rows);
        Assert.Equal(mine.Id, only.Id);
    }

    /// <summary>A one-off has no series, so the answer is an empty list rather than a 404: the client renders
    /// one shape either way.</summary>
    [Fact]
    public async Task SeriesRecordings_ReturnsEmptyForANonRecurringEvent()
    {
        var cal = new FakeCalendarClient
        {
            Event = new CalendarEvent("plain", "Coffee",
                DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"), null),
        };

        var result = await Build(cal, Guid.NewGuid()).SeriesRecordings("plain", default);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<SeriesRecordingDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(rows);
    }

    /// <summary>An id that resolves to nothing is a 404, matching the single-event endpoint beside it.</summary>
    [Fact]
    public async Task SeriesRecordings_404sWhenTheEventIsGone()
    {
        var result = await Build(new FakeCalendarClient { Event = null }, Guid.NewGuid())
            .SeriesRecordings("missing", default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static RecordingCalendarLink Link(Guid recordingId, string eventId, string seriesId, string startsAt) =>
        new()
        {
            RecordingId = recordingId,
            EventId = eventId,
            CalendarId = "primary",
            SeriesId = seriesId,
            StartsAt = DateTimeOffset.Parse(startsAt),
            EndsAt = DateTimeOffset.Parse(startsAt).AddMinutes(30),
        };
}
