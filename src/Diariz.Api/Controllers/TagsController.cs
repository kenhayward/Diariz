using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The caller's aggregated tag cloud: every tag across their recordings with a recording count,
/// summed weight, and the carrying recording ids. Ownership is transitive via <c>Recording.UserId</c> —
/// expressed as an explicit join so it works on both Npgsql and the in-memory test provider. Aggregation
/// happens in memory: it must be case-insensitive (LLM casing drift would otherwise split entries), which
/// is provider-agnostic that way, and the row count is small (≤ 12 per recording). If libraries ever grow
/// past a few thousand recordings, add a <c>?top=N</c> cap before reaching for a second endpoint.</summary>
[ApiController]
[Authorize]
[Route("api/tags")]
public class TagsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;
    public TagsController(DiarizDbContext db, IRoomScope rooms)
    {
        _db = db;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The aggregated tag cloud, case-insensitive (display = the most frequent casing), sorted by
    /// summed weight descending then tag text. With no <paramref name="roomId"/> it covers the caller's own
    /// library; with one it covers the recordings placed in that room (membership is the read gate - a
    /// non-member 404s).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TagCloudEntryDto>>> List([FromQuery] Guid? roomId = null)
    {
        IQueryable<Recording> recs;
        if (roomId is { } rid)
        {
            if (!await _rooms.IsMemberAsync(UserId, rid)) return NotFound();
            recs = _rooms.RecordingsIn(rid);
        }
        else
        {
            recs = _db.Recordings.Where(r => r.UserId == UserId);
        }

        var rows = await (
            from t in _db.RecordingTags
            join r in recs on t.RecordingId equals r.Id
            select new { t.Tag, t.Weight, t.RecordingId }).ToListAsync();

        var entries = rows
            .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCloudEntryDto(
                // Display the most frequent casing variant (ties: first seen) so drift doesn't flicker the UI.
                g.GroupBy(x => x.Tag, StringComparer.Ordinal)
                    .OrderByDescending(v => v.Count())
                    .First().Key,
                g.Select(x => x.RecordingId).Distinct().Count(),
                g.Sum(x => x.Weight),
                g.Select(x => x.RecordingId).Distinct().ToList()))
            .OrderByDescending(e => e.Weight)
            .ThenBy(e => e.Tag, StringComparer.Ordinal)
            .ToList();

        return entries;
    }
}
