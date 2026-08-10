using System.Text.Json;
using System.Text.Json.Serialization;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>One attendee as it travels on the wire and sits in <c>OutlookCalendarEvent.AttendeesJson</c>.
/// <see cref="Response"/> uses <b>Google's</b> vocabulary (accepted/declined/tentative/needsAction) rather than
/// Outlook's numeric <c>olResponseStatus</c>, so the shared <see cref="CalendarAttendee"/> projection and every
/// existing renderer work without translation.</summary>
public sealed record OutlookAttendeeJson(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("response")] string? Response = null,
    [property: JsonPropertyName("optional")] bool Optional = false);

/// <summary>Reads the signed-in user's mirrored desktop Outlook calendars and projects them into the same
/// <see cref="CalendarEvent"/> shape as Google and <c>.ics</c>, so the Calendar views, recording-to-meeting
/// matching and the event detail page do not care which source an event came from.
///
/// <para>Deliberately shaped like <see cref="IIcsCalendarClient"/>. The difference is where the data lives:
/// Google and <c>.ics</c> are fetched live at read time, whereas Outlook can only be reached from the user's
/// own PC, so its events are pushed by the desktop app and read back from our own tables here.</para></summary>
public interface IOutlookCalendarStore
{
    /// <summary>Events from all the user's <b>enabled</b> devices overlapping the window, ordered by start.
    /// Empty when the user has connected no device (never an error - the calendar degrades).</summary>
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>A single event by its public <c>outlook:{guid}</c> id, or null when the id belongs to another
    /// source, is unknown, or is not this user's.</summary>
    Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default);

    /// <summary>Whether the user has at least one enabled device - i.e. whether Outlook can contribute events
    /// at all. Used to decide if a calendar is connected without fetching anything.</summary>
    Task<bool> HasEnabledSourceAsync(Guid userId, CancellationToken ct = default);
}

public sealed class OutlookCalendarStore : IOutlookCalendarStore
{
    private readonly DiarizDbContext _db;

    public OutlookCalendarStore(DiarizDbContext db) => _db = db;

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
    {
        // Overlap, not containment: a meeting already under way when the window opens belongs to it. Matches
        // how the Google client asks for its range.
        var rows = await _db.OutlookCalendarEvents
            .Where(e => e.UserId == userId && e.Source!.Enabled && e.StartsAt < timeMax && e.EndsAt > timeMin)
            .OrderBy(e => e.StartsAt)
            .Select(e => new { Event = e, e.Source!.DisplayName, e.Source.Color })
            .ToListAsync(ct);

        return rows.Select(r => Project(r.Event, r.DisplayName, r.Color)).ToList();
    }

    public async Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
    {
        if (!OutlookEventId.TryParseEventKey(eventId, out var id)) return null;

        var row = await _db.OutlookCalendarEvents
            .Where(e => e.Id == id && e.UserId == userId)
            .Select(e => new { Event = e, e.Source!.DisplayName, e.Source.Color })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Project(row.Event, row.DisplayName, row.Color);
    }

    public Task<bool> HasEnabledSourceAsync(Guid userId, CancellationToken ct = default) =>
        _db.OutlookCalendarSources.AnyAsync(s => s.UserId == userId && s.Enabled, ct);

    /// <summary>The series key for a recurring occurrence: the desktop stores a recurring uid as
    /// <c>{seriesId}#{occurrenceStart}</c> (outlookSync.js), because Outlook returns one
    /// <c>GlobalAppointmentID</c> for the whole series rather than one per instance. Only ever called for a
    /// recurring row: <c>dedupeUids</c> gives a colliding <b>non</b>-recurring event the same suffix, so the
    /// separator alone does not mean "this repeats".</summary>
    private static string SeriesUid(string uid)
    {
        var hash = uid.IndexOf('#');
        return hash < 0 ? uid : uid[..hash];
    }

    private static CalendarEvent Project(Domain.Entities.OutlookCalendarEvent e, string? calendarName, string? color)
    {
        var (start, end) = Times(e);
        var attendees = ReadAttendees(e.AttendeesJson);

        var organizer = e.OrganizerEmail is null && e.OrganizerName is null
            ? null
            : new CalendarAttendee(e.OrganizerEmail, e.OrganizerName, null, Organizer: true);

        var recurring = e.IsRecurring;

        return new CalendarEvent(
            OutlookEventId.EventKey(e.Id),
            e.Subject,
            start,
            end,
            // A local Outlook appointment has no web permalink, so the join link is the only target worth
            // offering. Null when the meeting is not an online one.
            e.OnlineMeetingUrl,
            e.BodyText,
            e.Location,
            organizer,
            attendees,
            OutlookEventId.CalendarKey(e.SourceId),
            calendarName,
            color,
            e.AllDay,
            recurring,
            recurring ? SeriesUid(e.Uid) : null);
    }

    /// <summary>An all-day entry's display truth is its <b>local</b> date string, parsed exactly as the Google
    /// client parses its date-only <c>date</c> field, so day-grouping in the web app is one shared code path
    /// rather than three subtly different ones. Deriving the date from the stored UTC instant instead would
    /// reintroduce the classic off-by-one for every zone east of UTC.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) Times(Domain.Entities.OutlookCalendarEvent e)
    {
        if (e.AllDay &&
            DateTimeOffset.TryParse(e.StartDate, out var startDate) &&
            DateTimeOffset.TryParse(e.EndDate, out var endDate))
        {
            return (startDate, endDate);
        }
        return (e.StartsAt, e.EndsAt);
    }

    private static IReadOnlyList<CalendarAttendee> ReadAttendees(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<OutlookAttendeeJson>>(json);
            if (parsed is null) return [];
            return parsed
                .Select(a => new CalendarAttendee(a.Email, a.Name, a.Response))
                .ToList();
        }
        catch (JsonException)
        {
            // Malformed stored JSON must degrade to "no attendees", never break the whole calendar read.
            return [];
        }
    }
}
