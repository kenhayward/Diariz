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
    public async Task<IReadOnlyList<IcsFeedDto>> List() =>
        await _db.IcsCalendarSources
            .Where(s => s.UserId == UserId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new IcsFeedDto(s.Id, s.Name, s.Url, s.Color, s.Enabled, s.LastFetchedAt, s.LastError))
            .ToListAsync();

    [HttpPost]
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
