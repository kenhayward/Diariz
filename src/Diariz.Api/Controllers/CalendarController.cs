using System.Security.Claims;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Read-only calendar access for the signed-in user (drives the recordings Calendar tab). Merges the
/// user's Google calendars (when connected) with their external <c>.ics</c> feeds in a date window, or an
/// empty list when neither is available — so the tab degrades to recordings-only rather than erroring.</summary>
[ApiController]
[Authorize]
[Route("api/calendar")]
public class CalendarController : ControllerBase
{
    // The UI only ever asks for a single month grid (~6 weeks); cap the window so a crafted request can't
    // hammer the Calendar API.
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    private readonly IGoogleCalendarClient _calendar;
    private readonly IIcsCalendarClient _ics;

    public CalendarController(IGoogleCalendarClient calendar, IIcsCalendarClient ics)
    {
        _calendar = calendar;
        _ics = ics;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("events")]
    public async Task<ActionResult<IReadOnlyList<CalendarEvent>>> Events(
        [FromQuery] DateTimeOffset timeMin, [FromQuery] DateTimeOffset timeMax, CancellationToken ct)
    {
        if (timeMax <= timeMin) return BadRequest("timeMax must be after timeMin.");
        if (timeMax - timeMin > MaxRange) return BadRequest("Requested range is too large.");

        // Google null = not connected / token revoked; ICS feeds still show (and vice-versa). The two sources
        // are independent, so a user with only .ics feeds and no Google still gets a populated tab.
        var google = await _calendar.ListEventsAsync(UserId, timeMin, timeMax, ct) ?? [];
        var ics = await _ics.ListEventsAsync(UserId, timeMin, timeMax, ct);
        var merged = google.Concat(ics).OrderBy(e => e.Start).ToList();
        return Ok(merged);
    }

    /// <summary>A single event by id, with the full invite details (attendees, description, location,
    /// organizer). Powers the recording Overview's meeting details and the recording-less event preview.
    /// 404 when the event is missing or Calendar isn't connected.</summary>
    [HttpGet("events/{eventId}")]
    public async Task<ActionResult<CalendarEvent>> Event(string eventId, CancellationToken ct)
    {
        var ev = await _calendar.GetEventAsync(UserId, eventId, ct);
        if (ev is null) return NotFound();
        return Ok(ev);
    }
}
