namespace Diariz.Api.Services;

/// <summary>Matching a recording to the meeting it came from.
///
/// <para>Deliberately source-agnostic and pure. <see cref="CalendarEvent"/> carries no provider marker, so the
/// same rule applies to a Google event, an <c>.ics</c> feed event, and a mirrored desktop Outlook event alike -
/// which is the point of lifting this off the Google client, where it used to live and where only Google
/// events could ever reach it.</para></summary>
public static class CalendarMatching
{
    /// <summary>The timed event that overlaps the recording's time span the most, or null when none overlap.
    /// All-day entries are skipped outright: they blanket the whole day, so they would out-overlap every real
    /// meeting, and a holiday/birthday/out-of-office day is not the meeting a recording came from. (They can
    /// still be linked by hand from the picker.) Pure so it can be unit-tested without any calendar API.</summary>
    public static CalendarEvent? PickBest(IReadOnlyList<CalendarEvent> events, DateTimeOffset recStart, DateTimeOffset recEnd)
    {
        CalendarEvent? best = null;
        var bestOverlap = TimeSpan.Zero;
        foreach (var e in events)
        {
            if (e.AllDay) continue;
            var overlapStart = recStart > e.Start ? recStart : e.Start;
            var overlapEnd = recEnd < e.End ? recEnd : e.End;
            var overlap = overlapEnd - overlapStart;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = e;
            }
        }
        return best;
    }
}
