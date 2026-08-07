using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Picking the meeting a recording came from. Moved off <c>GoogleCalendarClientTests</c> with the
/// method itself - the rule was never Google-specific, but living on the Google client meant only Google
/// events could ever be scored by it.</summary>
public class CalendarMatchingTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso);

    [Fact]
    public void PickBest_ChoosesTheMostOverlappingEvent()
    {
        var events = new List<CalendarEvent>
        {
            new("a", "Barely", At("2026-07-02T08:00:00Z"), At("2026-07-02T09:05:00Z"), null),
            new("b", "Main", At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"), null),
        };
        // Recording 09:00-10:00: 'b' overlaps a full hour, 'a' only 5 min.
        var best = CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"));
        Assert.Equal("b", best!.Id);
    }

    [Fact]
    public void PickBest_ReturnsNull_WhenNothingOverlaps()
    {
        var events = new List<CalendarEvent>
        {
            new("x", "Earlier", At("2026-07-02T06:00:00Z"), At("2026-07-02T07:00:00Z"), null),
        };
        Assert.Null(CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));
    }

    [Fact]
    public void PickBest_IgnoresAllDayEvents_EvenWhenTheyOverlapMost()
    {
        var events = new List<CalendarEvent>
        {
            // An all-day event spans the whole recording, but it isn't a meeting - never match it.
            new("holiday", "Company holiday", At("2026-07-02T00:00:00Z"), At("2026-07-03T00:00:00Z"), null, AllDay: true),
            new("b", "Standup", At("2026-07-02T09:00:00Z"), At("2026-07-02T09:15:00Z"), null),
        };
        var best = CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"));
        Assert.Equal("b", best!.Id);
    }

    [Fact]
    public void PickBest_ReturnsNull_WhenOnlyAnAllDayEventOverlaps()
    {
        var events = new List<CalendarEvent>
        {
            new("holiday", "Company holiday", At("2026-07-02T00:00:00Z"), At("2026-07-03T00:00:00Z"), null, AllDay: true),
        };
        Assert.Null(CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));
    }

    /// <summary>The whole point of the move: an event from a subscribed <c>.ics</c> feed or a mirrored desktop
    /// Outlook calendar is scored exactly like a Google one. <see cref="CalendarEvent"/> carries no provider
    /// marker, so this holds by construction - the test pins it so a future "is this Google?" check cannot
    /// quietly creep back in.</summary>
    [Theory]
    [InlineData("ics:11111111-1111-1111-1111-111111111111:uid-a")]
    [InlineData("outlook:22222222-2222-2222-2222-222222222222")]
    public void PickBest_ScoresNonGoogleEventsTheSameWay(string id)
    {
        var events = new List<CalendarEvent>
        {
            new(id, "Team sync", At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"), null),
        };
        var best = CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"));
        Assert.Equal(id, best!.Id);
    }

    /// <summary>Overlap must be strictly positive: a meeting that ends exactly as the recording starts is
    /// adjacent, not the meeting it came from.</summary>
    [Fact]
    public void PickBest_IgnoresATouchingButNonOverlappingEvent()
    {
        var events = new List<CalendarEvent>
        {
            new("prior", "Ends as we start", At("2026-07-02T08:00:00Z"), At("2026-07-02T09:00:00Z"), null),
        };
        Assert.Null(CalendarMatching.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));
    }
}
