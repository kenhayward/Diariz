using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
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

    private static CalendarController Build(FakeCalendarClient cal, Guid userId, FakeIcsClient? ics = null) =>
        new(cal, ics ?? new FakeIcsClient()) { ControllerContext = Http.Context(userId) };

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
}
