using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Read-only calendar access for the signed-in user (drives the recordings Calendar tab). Merges every
/// source the user has - connected Google calendars, subscribed <c>.ics</c> feeds, and mirrored desktop Outlook
/// devices - in a date window, or an empty list when none is available, so the tab degrades to recordings-only
/// rather than erroring. The merge itself lives in <see cref="ICalendarAggregator"/> so that recording-to-meeting
/// matching sees exactly the same calendar this does.</summary>
[ApiController]
[Authorize]
[Route("api/calendar")]
public class CalendarController : ControllerBase
{
    // The UI only ever asks for a single month grid (~6 weeks); cap the window so a crafted request can't
    // hammer the Calendar API.
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    private readonly IGoogleCalendarClient _calendar;
    private readonly ICalendarAggregator _calendars;
    private readonly IGoogleCalendarSelectionStore _selection;

    public CalendarController(
        IGoogleCalendarClient calendar, ICalendarAggregator calendars, IGoogleCalendarSelectionStore selection)
    {
        // The Google client is still needed directly for the calendar *picker* below, which is Google-specific;
        // everything event-shaped goes through the aggregator.
        _calendar = calendar;
        _calendars = calendars;
        _selection = selection;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("events")]
    [EndpointSummary("List calendar events in a date range")]
    [EndpointDescription(
        "Your events between `timeMin` and `timeMax`, **merging connected Google calendars, subscribed `.ics` " +
        "feeds, and any desktop Outlook calendar you have mirrored** into one list sorted by start time. The " +
        "sources are independent, so someone with only feeds and no Google still gets a populated calendar.\n\n" +
        "Read-only: this never creates or changes anything in your calendar. An empty list is the normal " +
        "answer when nothing is connected - the calendar degrades rather than erroring.\n\n" +
        "The window must be positive and no wider than 62 days (400 otherwise), which is enough for the " +
        "six-week month grid the app draws.")]
    public async Task<ActionResult<IReadOnlyList<CalendarEvent>>> Events(
        [FromQuery] DateTimeOffset timeMin, [FromQuery] DateTimeOffset timeMax, CancellationToken ct)
    {
        if (timeMax <= timeMin) return BadRequest("timeMax must be after timeMin.");
        if (timeMax - timeMin > MaxRange) return BadRequest("Requested range is too large.");

        return Ok(await _calendars.ListEventsAsync(UserId, timeMin, timeMax, ct));
    }

    /// <summary>A single event by id, with the full invite details (attendees, description, location,
    /// organizer). Powers the recording Overview's meeting details and the recording-less event preview.
    /// Routed to whichever source the id belongs to; 404 when the event is missing or its calendar isn't
    /// connected. Previously Google-only, which made an <c>.ics</c> event's details 404.</summary>
    [HttpGet("events/{eventId}")]
    [EndpointSummary("Get one calendar event")]
    [EndpointDescription(
        "A single event with its **full invite details** - attendees, description, location, organiser - " +
        "which the range listing leaves out. Works for an event from any of your calendars: the id says which " +
        "source it came from.\n\n" +
        "A Google event is fetched **live**, so it reflects the invite as it stands now rather than a stored " +
        "snapshot. A `.ics` feed event is resolved from the feed, and a desktop Outlook one from the copy your " +
        "Windows app last mirrored.\n\n" +
        "404 when the event is gone, or the calendar it belongs to is not connected.")]
    public async Task<ActionResult<CalendarEvent>> Event(string eventId, CancellationToken ct)
    {
        var ev = await _calendars.GetEventAsync(UserId, eventId, ct);
        if (ev is null) return NotFound();
        return Ok(ev);
    }

    /// <summary>The user's Google calendars for the Preferences picker, each flagged with the user's effective
    /// selection (an unchosen selection defaults to the Google-visible calendars + primary). Empty when the
    /// user hasn't connected Calendar.</summary>
    [HttpGet("calendars")]
    [EndpointSummary("List your Google calendars")]
    [EndpointDescription(
        "Every Google calendar on your account, each flagged with whether it is **selected** for use here. " +
        "Until you choose explicitly, the selection defaults to the calendars visible in Google plus your " +
        "primary one, so the flag is always meaningful rather than empty.\n\n" +
        "Returns an empty list, not an error, when Calendar is not connected. Subscribed `.ics` feeds are " +
        "managed separately and do not appear here.")]
    public async Task<ActionResult<IReadOnlyList<CalendarListItemDto>>> Calendars(CancellationToken ct)
    {
        var all = await _calendar.ListAllCalendarsAsync(UserId, ct);
        if (all is null) return Ok(Array.Empty<CalendarListItemDto>());

        var selection = await _selection.GetSelectedIdsAsync(UserId, ct);
        var items = all.Select(c => new CalendarListItemDto(
            c.Id, c.Summary, c.BackgroundColor, c.Primary,
            Selected: selection is null ? (c.Selected || c.Primary) : selection.Contains(c.Id)));
        return Ok(items.ToList());
    }

    /// <summary>Save which Google calendars to consider for attribution + the overlay.</summary>
    [HttpPut("calendars")]
    [EndpointSummary("Choose which Google calendars to use")]
    [EndpointDescription(
        "Sets which calendars feed the overlay and the automatic matching of recordings to meetings - useful " +
        "for keeping a shared team calendar out of your meeting suggestions.\n\n" +
        "The list you send **replaces** the selection wholesale; sending an empty list selects none, which is " +
        "different from never having chosen (that defaults to your visible calendars). Nothing in Google is " +
        "changed.")]
    public async Task<IActionResult> SaveCalendars(SaveCalendarSelectionRequest req, CancellationToken ct)
    {
        await _selection.SetSelectedIdsAsync(UserId, req.Ids ?? [], ct);
        return NoContent();
    }
}
