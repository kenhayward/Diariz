using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Domain;
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
    public TagsController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>All the caller's tags, aggregated case-insensitively (display = the most frequent casing),
    /// sorted by summed weight descending then tag text.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TagCloudEntryDto>>> List()
    {
        var rows = await (
            from t in _db.RecordingTags
            join r in _db.Recordings on t.RecordingId equals r.Id
            where r.UserId == UserId
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
