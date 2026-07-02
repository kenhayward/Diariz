using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Diariz.Api.Services;

/// <summary>A calendar event as Diariz needs it (a slim projection of a Google Calendar event).</summary>
public record CalendarEvent(string Id, string? Summary, DateTimeOffset Start, DateTimeOffset End, string? HtmlLink);

/// <summary>Reads the signed-in user's primary Google Calendar (via their connected Google token) so a
/// recording can be matched to the meeting it was captured during. Read-only.</summary>
public interface IGoogleCalendarClient
{
    /// <summary>Events on the user's primary calendar overlapping <paramref name="timeMin"/>..<paramref name="timeMax"/>,
    /// or null if the user hasn't connected Calendar (token unavailable). Throws on a Calendar API error.</summary>
    Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);
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

    /// <summary>Project Google's events list into <see cref="CalendarEvent"/>s. Skips events without a
    /// parseable start/end. Handles both timed (<c>dateTime</c>) and all-day (<c>date</c>) events.</summary>
    public static List<CalendarEvent> ParseEvents(string json)
    {
        var events = new List<CalendarEvent>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return events;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id is null) continue;
            if (!TryReadTime(item, "start", out var start) || !TryReadTime(item, "end", out var end)) continue;

            var summary = item.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var htmlLink = item.TryGetProperty("htmlLink", out var h) ? h.GetString() : null;
            events.Add(new CalendarEvent(id, summary, start, end, htmlLink));
        }
        return events;
    }

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
