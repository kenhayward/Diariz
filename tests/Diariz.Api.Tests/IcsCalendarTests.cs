using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Pure parse+map of a raw .ics document into <see cref="CalendarEvent"/>s (no network). Recurring
/// events are expanded within the window; each event is tagged as coming from its feed source
/// (<c>ics:{sourceId}</c>) and coloured with the feed's colour, so ICS events sit alongside Google ones.</summary>
public class IcsCalendarTests
{
    // A timed event, an all-day event, and a daily-recurring event (3 instances).
    private const string Sample = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:evt-timed@test
        SUMMARY:Standup
        LOCATION:Room 1
        DESCRIPTION:Daily sync
        DTSTART:20260210T090000Z
        DTEND:20260210T093000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:evt-allday@test
        SUMMARY:Company holiday
        DTSTART;VALUE=DATE:20260212
        DTEND;VALUE=DATE:20260213
        END:VEVENT
        BEGIN:VEVENT
        UID:evt-recurring@test
        SUMMARY:Coffee
        DTSTART:20260209T140000Z
        DTEND:20260209T141500Z
        RRULE:FREQ=DAILY;COUNT=3
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly DateTimeOffset WindowStart = new(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);

    private static List<CalendarEvent> Parse() =>
        IcsCalendar.ParseEvents(Sample, WindowStart, WindowEnd, "src1", "Team", "#7986CB");

    [Fact]
    public void ParseEvents_TagsEveryEventWithSourceCalendarAndColour()
    {
        var events = Parse();
        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.Equal("ics:src1", e.CalendarId);
            Assert.Equal("Team", e.CalendarName);
            Assert.Equal("#7986CB", e.Color);
        });
    }

    [Fact]
    public void ParseEvents_MapsTimedEventFields()
    {
        var timed = Parse().Single(e => e.Summary == "Standup");
        Assert.Equal("Room 1", timed.Location);
        Assert.Equal("Daily sync", timed.Description);
        Assert.Equal(new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero), timed.Start);
        Assert.Equal(new DateTimeOffset(2026, 2, 10, 9, 30, 0, TimeSpan.Zero), timed.End);
    }

    [Fact]
    public void ParseEvents_IncludesAllDayEvent()
    {
        var holiday = Parse().Single(e => e.Summary == "Company holiday");
        Assert.Equal(new DateTimeOffset(2026, 2, 12, 0, 0, 0, TimeSpan.Zero), holiday.Start);
    }

    [Fact]
    public void ParseEvents_ExpandsRecurrenceIntoDistinctInstances()
    {
        var coffees = Parse().Where(e => e.Summary == "Coffee").ToList();
        // FREQ=DAILY;COUNT=3 starting 2026-02-09 -> 3 instances, all inside the window.
        Assert.Equal(3, coffees.Count);
        Assert.Equal(3, coffees.Select(e => e.Id).Distinct().Count()); // unique ids per instance
        Assert.Equal(3, coffees.Select(e => e.Start).Distinct().Count());
    }

    [Fact]
    public void ParseEvents_DropsEventsOutsideTheWindow()
    {
        // A window entirely before the sample's events yields nothing.
        var events = IcsCalendar.ParseEvents(
            Sample, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            "src1", "Team", "#7986CB");
        Assert.Empty(events);
    }

    [Fact]
    public void ParseEvents_ReturnsEmptyForGarbage()
    {
        Assert.Empty(IcsCalendar.ParseEvents("not an ics file", WindowStart, WindowEnd, "src1", "Team", null));
    }

    [Fact]
    public void FindEvent_ReturnsMatchingInstanceById()
    {
        var coffee = Parse().First(e => e.Summary == "Coffee");
        var found = IcsCalendar.FindEvent(Sample, WindowStart, WindowEnd, "src1", "Team", "#7986CB", coffee.Id);
        Assert.NotNull(found);
        Assert.Equal(coffee.Id, found!.Id);
        Assert.Equal(coffee.Start, found.Start);
    }
}
