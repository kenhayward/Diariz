using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Diariz.Api.Services;

/// <summary>One person on a calendar event (organizer or attendee).</summary>
public record CalendarAttendee(string? Email, string? DisplayName, string? ResponseStatus, bool Organizer = false, bool Self = false);

/// <summary>A calendar event as Diariz needs it (a projection of a Google Calendar event). The rich fields
/// (<see cref="Description"/>/<see cref="Location"/>/<see cref="Organizer"/>/<see cref="Attendees"/>) are
/// populated by <see cref="GoogleCalendarClient.GetEventAsync"/> and the events list. <see cref="CalendarId"/>/
/// <see cref="CalendarName"/>/<see cref="Color"/> identify which of the user's calendars the event is on (the
/// colour is that calendar's Google background hex) so the UI can show and colour events from every calendar.</summary>
public record CalendarEvent(
    string Id, string? Summary, DateTimeOffset Start, DateTimeOffset End, string? HtmlLink,
    string? Description = null, string? Location = null,
    CalendarAttendee? Organizer = null, IReadOnlyList<CalendarAttendee>? Attendees = null,
    string? CalendarId = null, string? CalendarName = null, string? Color = null);

/// <summary>One of the user's calendars from their calendarList (primary, secondary, shared/team, or a
/// subscribed feed). <see cref="BackgroundColor"/>/<see cref="ForegroundColor"/> are Google's hex colours.</summary>
public record CalendarListEntry(
    string Id, string? Summary, string? BackgroundColor, string? ForegroundColor, bool Selected, bool Primary);

/// <summary>Reads the signed-in user's Google Calendars (via their connected Google token) so recordings can
/// be matched to meetings and the Calendar tab can overlay them. Read-only; spans every calendar the user has
/// ticked visible (plus their primary), not just the primary.</summary>
public interface IGoogleCalendarClient
{
    /// <summary>Events across the user's selected calendars overlapping <paramref name="timeMin"/>..<paramref name="timeMax"/>,
    /// each tagged with its calendar id/name/colour, merged and ordered by start. Null if the user hasn't
    /// connected Calendar (token unavailable). Throws if the calendar list itself can't be read.</summary>
    Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>A single event by id (with rich fields), searched across the user's selected calendars, or null
    /// if the user hasn't connected Calendar or the event isn't found. Throws if the calendar list can't be read.</summary>
    Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default);

    /// <summary>The user's full calendarList (unfiltered by their Diariz selection), so the Preferences picker
    /// can offer every calendar. Null when the user hasn't connected Calendar.</summary>
    Task<IReadOnlyList<CalendarListEntry>?> ListAllCalendarsAsync(Guid userId, CancellationToken ct = default);
}

public class GoogleCalendarClient : IGoogleCalendarClient
{
    private const string CalendarListEndpoint = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
    private const string CalendarsBase = "https://www.googleapis.com/calendar/v3/calendars";

    private readonly HttpClient _http;
    private readonly IGoogleTokenProvider _tokens;
    private readonly IGoogleCalendarSelectionStore _selection;

    public GoogleCalendarClient(HttpClient http, IGoogleTokenProvider tokens, IGoogleCalendarSelectionStore selection)
    {
        _http = http;
        _tokens = tokens;
        _selection = selection;
    }

    public async Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null; // not connected / refresh failed — caller prompts to reconnect

        var calendars = await ListCalendarsAsync(access, userId, ct);

        // Fetch each calendar's events in parallel and tag them with that calendar's id/name/colour. A single
        // flaky calendar (e.g. a shared one that 403s) is skipped, not fatal — the rest still show.
        var perCalendar = await Task.WhenAll(calendars.Select(async cal =>
        {
            try
            {
                var events = await ListRawEventsAsync(access, cal.Id, timeMin, timeMax, ct);
                return events.Select(e => e with { CalendarId = cal.Id, CalendarName = cal.Summary, Color = cal.BackgroundColor });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Enumerable.Empty<CalendarEvent>();
            }
        }));

        return perCalendar.SelectMany(e => e).OrderBy(e => e.Start).ToList();
    }

    public async Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null; // not connected / refresh failed

        // Try the primary first (the common case), then the other selected calendars until the event is found.
        var calendars = (await ListCalendarsAsync(access, userId, ct)).OrderByDescending(c => c.Primary);
        foreach (var cal in calendars)
        {
            var ev = await GetEventFromCalendarAsync(access, cal.Id, eventId, ct);
            if (ev is not null)
                return ev with { CalendarId = cal.Id, CalendarName = cal.Summary, Color = cal.BackgroundColor };
        }
        return null;
    }

    public async Task<IReadOnlyList<CalendarListEntry>?> ListAllCalendarsAsync(Guid userId, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null;
        return await FetchCalendarListAsync(access, ct);
    }

    /// <summary>The user's calendars to enumerate for events, narrowed to their stored Diariz selection (or,
    /// when they haven't chosen, the calendars they've made visible in Google plus their primary).</summary>
    private async Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string access, Guid userId, CancellationToken ct)
    {
        var all = await FetchCalendarListAsync(access, ct);
        var selection = await _selection.GetSelectedIdsAsync(userId, ct);
        return ApplySelection(all, selection);
    }

    /// <summary>Narrow the full calendar list to the ones to enumerate. A null <paramref name="selection"/>
    /// (user hasn't chosen) keeps the Google-visible calendars plus the primary; otherwise keeps exactly the
    /// user's chosen ids. Pure, so it's unit-testable without the Calendar API.</summary>
    public static IReadOnlyList<CalendarListEntry> ApplySelection(
        IReadOnlyList<CalendarListEntry> all, IReadOnlySet<string>? selection) =>
        (selection is null
            ? all.Where(c => c.Selected || c.Primary)
            : all.Where(c => selection.Contains(c.Id)))
        .ToList();

    /// <summary>Fetch + parse the user's full calendarList (unfiltered).</summary>
    private async Task<IReadOnlyList<CalendarListEntry>> FetchCalendarListAsync(string access, CancellationToken ct)
    {
        var url = QueryHelpers.AddQueryString(CalendarListEndpoint, new Dictionary<string, string?>
        {
            ["minAccessRole"] = "reader",
            ["showHidden"] = "false",
        });
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Calendar list failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
        }
        return ParseCalendarList(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Project a Google calendarList response into <see cref="CalendarListEntry"/>s. Pure so it's
    /// unit-testable without the Calendar API.</summary>
    public static List<CalendarListEntry> ParseCalendarList(string json)
    {
        var list = new List<CalendarListEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id is null) continue;
            list.Add(new CalendarListEntry(
                id,
                item.TryGetProperty("summary", out var s) ? s.GetString() : null,
                item.TryGetProperty("backgroundColor", out var bg) ? bg.GetString() : null,
                item.TryGetProperty("foregroundColor", out var fg) ? fg.GetString() : null,
                item.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.True,
                item.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    private async Task<List<CalendarEvent>> ListRawEventsAsync(
        string access, string calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct)
    {
        // Page through the whole window. Google caps each response at `maxResults`; a busy calendar easily
        // exceeds it, and `orderBy=startTime` returns the EARLIEST first - so without following nextPageToken
        // the later (future) events were silently dropped. singleEvents expands recurring series into instances.
        var results = new List<CalendarEvent>();
        string? pageToken = null;
        for (var page = 0; page < 20; page++) // safety cap: 20 x 250 = 5000 events per calendar per window
        {
            var query = new Dictionary<string, string?>
            {
                ["singleEvents"] = "true",
                ["orderBy"] = "startTime",
                ["maxResults"] = "250",
                ["timeMin"] = timeMin.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["timeMax"] = timeMax.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            if (pageToken is not null) query["pageToken"] = pageToken;
            var url = QueryHelpers.AddQueryString($"{CalendarsBase}/{Uri.EscapeDataString(calendarId)}/events", query);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Calendar events failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
            }

            var (events, next) = ParseEventsPage(await resp.Content.ReadAsStringAsync(ct));
            results.AddRange(events);
            if (string.IsNullOrEmpty(next)) break;
            pageToken = next;
        }
        return results;
    }

    /// <summary>GET one event from a specific calendar, or null if it isn't there (404/410) or that calendar
    /// errors (so <see cref="GetEventAsync"/> keeps searching the others).</summary>
    private async Task<CalendarEvent?> GetEventFromCalendarAsync(string access, string calendarId, string eventId, CancellationToken ct)
    {
        var url = $"{CalendarsBase}/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null; // not on this calendar (404/410) or unreadable — try the next
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseEvent(doc.RootElement);
    }

    /// <summary>Project Google's events list into <see cref="CalendarEvent"/>s. Skips events without a
    /// parseable start/end. Handles both timed (<c>dateTime</c>) and all-day (<c>date</c>) events.</summary>
    public static List<CalendarEvent> ParseEvents(string json) => ParseEventsPage(json).Events;

    /// <summary>Parse one events-list page into its events plus the <c>nextPageToken</c> (null on the last page)
    /// so the caller can page through a large window. Pure, for unit tests.</summary>
    public static (List<CalendarEvent> Events, string? NextPageToken) ParseEventsPage(string json)
    {
        var events = new List<CalendarEvent>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (ParseEvent(item) is { } e)
                    events.Add(e);

        var next = doc.RootElement.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        return (events, next);
    }

    /// <summary>Project a single Google event object into a <see cref="CalendarEvent"/> (rich fields included),
    /// or null when it has no parseable start/end. Pure so it's unit-testable without the Calendar API.</summary>
    public static CalendarEvent? ParseEvent(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (id is null) return null;
        if (!TryReadTime(item, "start", out var start) || !TryReadTime(item, "end", out var end)) return null;

        var summary = item.TryGetProperty("summary", out var s) ? s.GetString() : null;
        var htmlLink = item.TryGetProperty("htmlLink", out var h) ? h.GetString() : null;
        var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
        var location = item.TryGetProperty("location", out var l) ? l.GetString() : null;

        var organizer = item.TryGetProperty("organizer", out var org) && org.ValueKind == JsonValueKind.Object
            ? ReadAttendee(org)
            : null;

        var attendees = new List<CalendarAttendee>();
        if (item.TryGetProperty("attendees", out var att) && att.ValueKind == JsonValueKind.Array)
            foreach (var a in att.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object)
                    attendees.Add(ReadAttendee(a));

        return new CalendarEvent(id, summary, start, end, htmlLink, description, location, organizer, attendees);
    }

    private static CalendarAttendee ReadAttendee(JsonElement a) => new(
        a.TryGetProperty("email", out var em) ? em.GetString() : null,
        a.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
        a.TryGetProperty("responseStatus", out var rs) ? rs.GetString() : null,
        a.TryGetProperty("organizer", out var o) && o.ValueKind == JsonValueKind.True,
        a.TryGetProperty("self", out var se) && se.ValueKind == JsonValueKind.True);

    /// <summary>The event that overlaps the recording's time span the most, or null when none overlap.
    /// Pure so it can be unit-tested without the Calendar API.</summary>
    public static CalendarEvent? PickBest(IReadOnlyList<CalendarEvent> events, DateTimeOffset recStart, DateTimeOffset recEnd)
    {
        CalendarEvent? best = null;
        var bestOverlap = TimeSpan.Zero;
        foreach (var e in events)
        {
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

    private static bool TryReadTime(JsonElement item, string name, out DateTimeOffset value)
    {
        value = default;
        if (!item.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) return false;
        // Timed events carry `dateTime` (RFC-3339 with offset); all-day events carry a `date` (yyyy-MM-dd).
        if (node.TryGetProperty("dateTime", out var dt) && dt.GetString() is { } dts &&
            DateTimeOffset.TryParse(dts, out value)) return true;
        if (node.TryGetProperty("date", out var d) && d.GetString() is { } ds &&
            DateTimeOffset.TryParse(ds, out value)) return true;
        return false;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
