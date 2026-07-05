using Ical.Net;
using Ical.Net.DataTypes;
// Ical.Net's event type name collides with Diariz's own CalendarEvent record (same Services namespace).
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Diariz.Api.Services;

/// <summary>Pure parse+map of a raw iCalendar (<c>.ics</c>) document into Diariz <see cref="CalendarEvent"/>s.
/// Recurring events are expanded within the requested window (via Ical.Net's recurrence evaluator), and every
/// event is tagged as coming from its feed source (<c>CalendarId = "ics:{sourceId}"</c>) and coloured with the
/// feed's colour, so external-feed events sit alongside the user's Google events. No network here - the fetch
/// (with its SSRF guard) lives in the calendar client; this stays a testable transform of ICS text.</summary>
public static class IcsCalendar
{
    // Safety cap: even with a bounded window a pathological RRULE could yield a huge instance count.
    private const int MaxInstances = 1000;

    /// <summary>Project the events in <paramref name="icsText"/> that fall in [<paramref name="windowStart"/>,
    /// <paramref name="windowEnd"/>) into <see cref="CalendarEvent"/>s, expanding recurrences. Malformed input
    /// yields an empty list (never throws).</summary>
    public static List<CalendarEvent> ParseEvents(
        string icsText, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        string sourceId, string? calendarName, string? color)
    {
        var results = new List<CalendarEvent>();
        Calendar? cal;
        try
        {
            cal = Calendar.Load(icsText);
        }
        catch
        {
            return results; // not a parseable calendar
        }
        if (cal is null) return results;

        var calendarId = $"ics:{sourceId}";
        var endUtc = windowEnd.UtcDateTime;
        var start = new CalDateTime(windowStart.UtcDateTime, "UTC");

        try
        {
            foreach (var occ in cal.GetOccurrences(start).TakeWhile(o => o.Period.StartTime.AsUtc < endUtc).Take(MaxInstances))
            {
                if (occ.Source is not IcalEvent source) continue;

                var startAt = new DateTimeOffset(occ.Period.StartTime.AsUtc, TimeSpan.Zero);
                var effEnd = occ.Period.EffectiveEndTime?.AsUtc ?? occ.Period.StartTime.AsUtc;
                var endAt = new DateTimeOffset(effEnd, TimeSpan.Zero);

                results.Add(new CalendarEvent(
                    Id: MakeId(source, startAt),
                    Summary: NullIfBlank(source.Summary),
                    Start: startAt,
                    End: endAt,
                    HtmlLink: source.Url?.ToString(),
                    Description: NullIfBlank(source.Description),
                    Location: NullIfBlank(source.Location),
                    Organizer: null,
                    Attendees: null,
                    CalendarId: calendarId,
                    CalendarName: calendarName,
                    Color: color));
            }
        }
        catch
        {
            // A broken recurrence rule shouldn't sink the whole feed - keep whatever we mapped so far.
        }

        return results;
    }

    /// <summary>The single event in the feed whose id matches <paramref name="eventId"/> (a recurrence instance
    /// id round-trips), with its rich fields, or null if absent. Mirrors the Google client's GetEvent.</summary>
    public static CalendarEvent? FindEvent(
        string icsText, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        string sourceId, string? calendarName, string? color, string eventId) =>
        ParseEvents(icsText, windowStart, windowEnd, sourceId, calendarName, color)
            .FirstOrDefault(e => e.Id == eventId);

    /// <summary>A stable per-instance id: the raw UID for a one-off, UID + occurrence timestamp for a recurring
    /// series (so each instance is addressable), mirroring Google's expanded-instance ids.</summary>
    private static string MakeId(IcalEvent source, DateTimeOffset startAt)
    {
        var uid = string.IsNullOrWhiteSpace(source.Uid)
            ? $"noid-{startAt.UtcDateTime:yyyyMMddTHHmmssZ}"
            : source.Uid;
        var recurring = source.RecurrenceRule is not null;
        return recurring ? $"{uid}_{startAt.UtcDateTime:yyyyMMddTHHmmssZ}" : uid;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
