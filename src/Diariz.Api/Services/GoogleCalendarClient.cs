using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Diariz.Api.Services;

/// <summary>One person on a calendar event (organizer or attendee).</summary>
public record CalendarAttendee(string? Email, string? DisplayName, string? ResponseStatus, bool Organizer = false, bool Self = false);

/// <summary>A calendar event as Diariz needs it (a projection of a Google Calendar event). The rich fields
/// (<see cref="Description"/>/<see cref="Location"/>/<see cref="Organizer"/>/<see cref="Attendees"/>) are
/// populated by <see cref="GoogleCalendarClient.GetEventAsync"/> and the events list, and are what the
/// Overview + event-preview surfaces show so the user needn't open Google Calendar.</summary>
public record CalendarEvent(
    string Id, string? Summary, DateTimeOffset Start, DateTimeOffset End, string? HtmlLink,
    string? Description = null, string? Location = null,
    CalendarAttendee? Organizer = null, IReadOnlyList<CalendarAttendee>? Attendees = null);

/// <summary>Reads the signed-in user's primary Google Calendar (via their connected Google token) so a
/// recording can be matched to the meeting it was captured during. Read-only.</summary>
public interface IGoogleCalendarClient
{
    /// <summary>Events on the user's primary calendar overlapping <paramref name="timeMin"/>..<paramref name="timeMax"/>,
    /// or null if the user hasn't connected Calendar (token unavailable). Throws on a Calendar API error.</summary>
    Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>A single event by id (with rich fields), or null if the user hasn't connected Calendar or the
    /// event no longer exists (404/410). Throws on any other Calendar API error.</summary>
    Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default);
}

public class GoogleCalendarClient : IGoogleCalendarClient
{
    private const string EventsEndpoint = "https://www.googleapis.com/calendar/v3/calendars/primary/events";

    private readonly HttpClient _http;
    private readonly IGoogleTokenProvider _tokens;

    public GoogleCalendarClient(HttpClient http, IGoogleTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null; // not connected / refresh failed — caller prompts to reconnect

        // singleEvents expands recurring series into instances; RFC-3339 bounds keep the window tight.
        var url = QueryHelpers.AddQueryString(EventsEndpoint, new Dictionary<string, string?>
        {
            ["singleEvents"] = "true",
            ["orderBy"] = "startTime",
            ["maxResults"] = "50",
            ["timeMin"] = timeMin.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["timeMax"] = timeMax.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        });

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Calendar list failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
        }
        return ParseEvents(await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null; // not connected / refresh failed

        var url = $"{EventsEndpoint}/{Uri.EscapeDataString(eventId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await _http.SendAsync(req, ct);
        // The event was deleted/cancelled since we linked it — treat as "no longer available", not an error.
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Calendar get failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseEvent(doc.RootElement);
    }

    /// <summary>Project Google's events list into <see cref="CalendarEvent"/>s. Skips events without a
    /// parseable start/end. Handles both timed (<c>dateTime</c>) and all-day (<c>date</c>) events.</summary>
    public static List<CalendarEvent> ParseEvents(string json)
    {
        var events = new List<CalendarEvent>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return events;

        foreach (var item in items.EnumerateArray())
            if (ParseEvent(item) is { } e)
                events.Add(e);
        return events;
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
