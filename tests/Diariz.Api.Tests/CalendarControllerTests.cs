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
        public DateTimeOffset? TimeMin { get; private set; }
        public DateTimeOffset? TimeMax { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
        {
            TimeMin = timeMin; TimeMax = timeMax;
            return Task.FromResult(Events);
        }
    }

    private static CalendarController Build(FakeCalendarClient cal, Guid userId) =>
        new(cal) { ControllerContext = Http.Context(userId) };

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
}
