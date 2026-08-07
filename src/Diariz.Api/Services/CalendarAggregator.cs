using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>The one place the user's calendar sources are combined - connected Google calendars, subscribed
/// <c>.ics</c> feeds, and mirrored desktop Outlook devices - so every caller sees the same calendar.
///
/// <para>This merge used to live inline in <c>CalendarController.Events</c>, which meant only the Calendar tab
/// ever saw all of it. Recording-to-meeting matching lives in a different controller and called the Google
/// client directly, so a user with only <c>.ics</c> feeds could not have a recording matched at all, and an
/// <c>.ics</c> event's detail fetch 404'd. Lifting the merge here fixes both, and is what lets Outlook
/// participate without touching either controller's logic again.</para></summary>
/// <summary>A merged calendar read, plus the one piece of per-source health a caller may need to act on.
/// <para><see cref="GoogleUnavailable"/> means the user granted Google but no token could be obtained - a
/// revoked or expired refresh token. Everything degrades quietly except this: it is the one failure the user
/// can actually fix, and silently returning "no meetings" would hide a broken connection indefinitely.</para></summary>
public sealed record CalendarFetch(IReadOnlyList<CalendarEvent> Events, bool GoogleUnavailable);

public interface ICalendarAggregator
{
    /// <summary>Every source's events overlapping the window, merged and ordered by start. Empty rather than an
    /// error when nothing is connected - the calendar degrades rather than failing.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>As <see cref="ListEventsAsync"/>, but also reporting whether Google came back unavailable, so a
    /// caller that has something useful to say about it can.</summary>
    Task<CalendarFetch> FetchAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>A single event by id, routed to whichever source the id belongs to. Null when it is unknown or
    /// the owning source is not connected.</summary>
    Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default);

    /// <summary>Whether the user has any calendar at all - a Google grant, an enabled feed, or an enabled
    /// Outlook device. Gates the matching endpoints, replacing the Google-only check that locked out everyone
    /// else.</summary>
    Task<bool> HasAnySourceAsync(Guid userId, CancellationToken ct = default);
}

public sealed class CalendarAggregator : ICalendarAggregator
{
    /// <summary>How far either side of a single event to look when resolving an <c>.ics</c> id. Feeds are
    /// parsed wholesale rather than fetched by id, so a lookup has to list a window and pick from it; a year
    /// covers any event a user could be looking at without pulling a decade of a busy team calendar.</summary>
    private static readonly TimeSpan IcsLookupWindow = TimeSpan.FromDays(365);

    private readonly IGoogleCalendarClient _google;
    private readonly IIcsCalendarClient _ics;
    private readonly IOutlookCalendarStore _outlook;
    private readonly DiarizDbContext _db;

    public CalendarAggregator(
        IGoogleCalendarClient google, IIcsCalendarClient ics, IOutlookCalendarStore outlook, DiarizDbContext db)
    {
        _google = google;
        _ics = ics;
        _outlook = outlook;
        _db = db;
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
        (await FetchAsync(userId, timeMin, timeMax, ct)).Events;

    public async Task<CalendarFetch> FetchAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
    {
        // Every source is independent: Google returning null means "not connected / token revoked", and a user
        // with only feeds or only Outlook must still get a populated calendar.
        var google = await _google.ListEventsAsync(userId, timeMin, timeMax, ct);
        var ics = await _ics.ListEventsAsync(userId, timeMin, timeMax, ct);
        var outlook = await _outlook.ListEventsAsync(userId, timeMin, timeMax, ct);

        var merged = (google ?? []).Concat(ics).Concat(outlook).OrderBy(e => e.Start).ToList();
        return new CalendarFetch(merged, GoogleUnavailable: google is null);
    }

    public async Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(eventId)) return null;

        // Routed on the id's scheme, which each source stamps on the ids it produces.
        if (eventId.StartsWith(OutlookEventId.Prefix, StringComparison.Ordinal))
            return await _outlook.GetEventAsync(userId, eventId, ct);

        if (eventId.StartsWith("ics:", StringComparison.Ordinal))
        {
            // No by-id fetch exists for a feed - an .ics is parsed as a whole - so resolve it out of a listing.
            var now = DateTimeOffset.UtcNow;
            var events = await _ics.ListEventsAsync(userId, now - IcsLookupWindow, now + IcsLookupWindow, ct);
            return events.FirstOrDefault(e => e.Id == eventId);
        }

        return await _google.GetEventAsync(userId, eventId, ct);
    }

    public async Task<bool> HasAnySourceAsync(Guid userId, CancellationToken ct = default)
    {
        // Answered from the database rather than by probing the clients: this only gates "can we offer to match
        // a meeting", and it would be absurd for that to cost a Google round trip plus every feed fetch.
        if (await _db.UserSettings.AnyAsync(s => s.UserId == userId && s.GoogleCalendarGranted, ct)) return true;
        if (await _db.IcsCalendarSources.AnyAsync(s => s.UserId == userId && s.Enabled, ct)) return true;
        return await _db.OutlookCalendarSources.AnyAsync(s => s.UserId == userId && s.Enabled, ct);
    }
}
