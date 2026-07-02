using System.Security.Claims;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Read-only Google Calendar access for the signed-in user (drives the recordings Calendar tab).
/// Returns the user's primary-calendar events in a date window, or an empty list when Calendar isn't
/// connected — so the tab degrades to recordings-only rather than erroring.</summary>
[ApiController]
[Authorize]
[Route("api/calendar")]
public class CalendarController : ControllerBase
{
    // The UI only ever asks for a single month grid (~6 weeks); cap the window so a crafted request can't
    // hammer the Calendar API.
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    private readonly IGoogleCalendarClient _calendar;

    public CalendarController(IGoogleCalendarClient calendar) => _calendar = calendar;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("events")]
    public async Task<ActionResult<IReadOnlyList<CalendarEvent>>> Events(
        [FromQuery] DateTimeOffset timeMin, [FromQuery] DateTimeOffset timeMax, CancellationToken ct)
    {
        if (timeMax <= timeMin) return BadRequest("timeMax must be after timeMin.");
        if (timeMax - timeMin > MaxRange) return BadRequest("Requested range is too large.");

        // Null = not connected / token revoked → empty list (the Calendar tab still shows recordings).
        var events = await _calendar.ListEventsAsync(UserId, timeMin, timeMax, ct);
        return Ok(events ?? []);
    }
}
