using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Manages the signed-in user's external iCalendar (<c>.ics</c>) feed subscriptions - public team or
/// shared calendars not reachable through their Google account. Their events are merged into the Calendar
/// views by <see cref="IIcsCalendarClient"/>. Every feed URL is validated (https only) and test-fetched
/// behind the SSRF guard before it's stored, so a broken or unsafe URL is rejected up front.</summary>
[ApiController]
[Authorize]
[Route("api/calendar/feeds")]
public class CalendarFeedsController : ControllerBase
{
    /// <summary>A generous per-user cap so a runaway client can't create unbounded feeds.</summary>
    public const int MaxFeedsPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IIcsCalendarClient _ics;

    public CalendarFeedsController(DiarizDbContext db, IIcsCalendarClient ics)
    {
        _db = db;
        _ics = ics;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IcsFeedDto ToDto(IcsCalendarSource s) =>
        new(s.Id, s.Name, s.Url, s.Color, s.Enabled, s.LastFetchedAt, s.LastError);

    [HttpGet]
    [EndpointSummary("List your calendar feed subscriptions")]
    [EndpointDescription(
        "The external iCalendar (`.ics`) feeds you subscribe to - a public team calendar, a room booking " +
        "system, anything not reachable through your Google account. Their events merge into the calendar " +
        "views alongside Google's.\n\n" +
        "Each carries its health: when it was last fetched and the last error, if any. A feed that has " +
        "started failing shows up here rather than just quietly going empty.")]
    public async Task<IReadOnlyList<IcsFeedDto>> List() =>
        await _db.IcsCalendarSources
            .Where(s => s.UserId == UserId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new IcsFeedDto(s.Id, s.Name, s.Url, s.Color, s.Enabled, s.LastFetchedAt, s.LastError))
            .ToListAsync();

    [HttpPost]
    [EndpointSummary("Subscribe to a calendar feed")]
    [EndpointDescription(
        "Adds an `.ics` subscription. The URL is **validated and test-fetched before it is stored**, so a " +
        "typo or an unreachable feed is rejected up front (400 with the reason) rather than failing silently " +
        "later.\n\n" +
        "`https` only, and the URL must resolve to a public address - internal and loopback addresses are " +
        "refused, so a feed cannot be used to probe the server's own network. 400 for an empty name or when " +
        "you have reached the per-user feed limit.")]
    public async Task<ActionResult<IcsFeedDto>> Create(IcsFeedRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest("A calendar name is required.");
        if (name.Length > 128) name = name[..128];

        if (await _db.IcsCalendarSources.CountAsync(s => s.UserId == UserId, ct) >= MaxFeedsPerUser)
            return BadRequest("Calendar-feed limit reached. Remove a feed before adding another.");

        var (ok, error) = await _ics.ProbeAsync(req.Url ?? "", ct);
        if (!ok) return BadRequest(error);

        var row = new IcsCalendarSource
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = name,
            Url = req.Url!.Trim(),
            Color = Normalize(req.Color),
            Enabled = req.Enabled,
        };
        _db.IcsCalendarSources.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a calendar feed subscription")]
    [EndpointDescription(
        "Changes a feed's name, colour, URL, or whether it is enabled. Disabling keeps the subscription but " +
        "stops its events appearing - the reversible alternative to removing one.\n\n" +
        "The URL is only re-validated when it actually **changes**, so a feed that is currently broken can " +
        "still be renamed or disabled; a new URL clears the stored error so its health is re-evaluated on the " +
        "next fetch.")]
    public async Task<ActionResult<IcsFeedDto>> Update(Guid id, IcsFeedRequest req, CancellationToken ct)
    {
        var row = await _db.IcsCalendarSources.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (row is null) return NotFound();

        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest("A calendar name is required.");
        if (name.Length > 128) name = name[..128];

        var newUrl = (req.Url ?? "").Trim();
        // Only re-validate/test-fetch when the URL actually changed (an unreachable feed can still be renamed
        // or disabled). A changed URL clears the stale error so the next read re-evaluates health.
        if (newUrl != row.Url)
        {
            var (ok, error) = await _ics.ProbeAsync(newUrl, ct);
            if (!ok) return BadRequest(error);
            row.Url = newUrl;
            row.LastError = null;
            row.LastFetchedAt = null;
        }

        row.Name = name;
        row.Color = Normalize(req.Color);
        row.Enabled = req.Enabled;
        await _db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Remove a calendar feed subscription")]
    [EndpointDescription(
        "Unsubscribes: the feed's events stop appearing. Nothing is deleted at the source, and any recording " +
        "already linked to one of its events keeps that link, since links store a snapshot. To hide a feed " +
        "temporarily, disable it instead.")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.IcsCalendarSources.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (row is null) return NotFound();
        _db.IcsCalendarSources.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Normalize(string? color)
    {
        var c = color?.Trim();
        return string.IsNullOrEmpty(c) ? null : (c.Length > 32 ? c[..32] : c);
    }
}
