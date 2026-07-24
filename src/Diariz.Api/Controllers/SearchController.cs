using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Search over the caller's transcripts, for the left nav's search bar.
///
/// The engine (<see cref="ITranscriptSearch"/>) already existed but was reachable only through the chat/MCP
/// tools, which format results as markdown for a model to read. This is the same engine projected as JSON for
/// a UI: structured hits with a snippet, the moment it occurs, and where the recording lives.</summary>
[ApiController]
[Authorize]
[Route("api/search")]
public class SearchController : ControllerBase
{
    /// <summary>Rows returned when the caller doesn't ask. Deliberately below
    /// <see cref="TranscriptSearch.MaxLimit"/>: a nav panel shows a glanceable list, not a report.</summary>
    private const int DefaultLimit = 20;

    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;
    private readonly ITranscriptSearch _search;

    public SearchController(DiarizDbContext db, IRoomScope rooms, ITranscriptSearch search)
    {
        _db = db;
        _rooms = rooms;
        _search = search;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Search transcripts, and folder names, within a scope.</summary>
    /// <param name="q">The query. Required.</param>
    /// <param name="roomId">Restrict to one room. Ignored when <paramref name="everywhere"/> is set.</param>
    /// <param name="sectionId">Restrict to this folder and its sub-folders. Ignored when
    /// <paramref name="everywhere"/> is set.</param>
    /// <param name="everywhere">Search every room the caller can see. Wins over the other two scopes.</param>
    /// <param name="speaker">Only passages spoken by this person. Forces the lexical-only path.</param>
    [HttpGet]
    [EndpointSummary("Search transcripts and folders")]
    [EndpointDescription(
        "Searches across what you can see and returns two things: **transcript hits** (a snippet, the moment " +
        "it occurs, and the recording and folder it lives in, so you can open the transcript at that point) " +
        "and **folder hits** whose names match.\n\n" +
        "Keyword search by default, fused with semantic (meaning-based) matching when an embeddings endpoint " +
        "is configured - your own, or the platform default. The same query works either way, so a caller " +
        "never chooses between them.\n\n" +
        "**Scope.** By default the search covers every room you belong to. `sectionId` narrows it to one folder " +
        "and everything beneath it; `roomId` narrows it to one room; `everywhere` overrides **both** rather " +
        "than combining with them. A folder you cannot see yields no transcript hits rather than silently " +
        "widening the search.\n\n" +
        "`from`/`to` filter by recording date and `speaker` restricts to passages spoken by one person - note " +
        "that filtering by speaker forces the keyword path, so it will not do semantic matching. `limit` " +
        "defaults to 20 and is clamped to 50. An empty query is rejected with 400.")]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery] string q,
        [FromQuery] Guid? roomId = null,
        [FromQuery] Guid? sectionId = null,
        [FromQuery] bool everywhere = false,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? speaker = null,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0) return BadRequest("A search query is required.");
        limit = Math.Clamp(limit, 1, TranscriptSearch.MaxLimit);

        // `everywhere` wins outright rather than intersecting: one simple, stateable rule.
        if (everywhere) { roomId = null; sectionId = null; }

        var visibleRooms = (await _rooms.RoomIdsForUserAsync(UserId, ct)).ToHashSet();
        var scope = everywhere ? "everywhere" : sectionId is not null ? "folder" : roomId is not null ? "room" : "everywhere";

        // Folder scope: resolve the folder subtree to the recordings in it.
        IReadOnlyList<Guid>? recordingScope = null;
        if (sectionId is Guid sid)
        {
            var sectionIds = await SubtreeAsync(sid, visibleRooms, ct);
            // Filing is a per-room *placement*, not a property of the recording: the same recording can sit in
            // a folder in one room and be ungrouped in another. So the scope comes from RoomRecordings.
            recordingScope = sectionIds.Count == 0
                ? []
                : await _db.RoomRecordings
                    .Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value))
                    .Select(p => p.RecordingId).Distinct().ToListAsync(ct);

            // An *empty* scope means "nothing to search". It must not reach TranscriptSearch, which reads an
            // empty scope as "unscoped" and would search the caller's entire library instead - the opposite
            // of what was asked. Short-circuit.
            if (recordingScope.Count == 0)
                return new SearchResponse(query, scope, await FoldersAsync(query, visibleRooms, ct), []);
        }

        var hits = await _search.SearchAsync(UserId, query, speaker, recordingScope, limit, roomId, ct);

        return new SearchResponse(
            query, scope,
            await FoldersAsync(query, visibleRooms, ct),
            await ProjectAsync(hits, visibleRooms, from, to, ct));
    }

    /// <summary>A folder plus all its descendants, restricted to rooms the caller can see. Empty for an unknown
    /// or invisible folder - which the caller reads as "nothing here", never as "search everything".</summary>
    private async Task<IReadOnlyList<Guid>> SubtreeAsync(Guid rootId, HashSet<Guid> visibleRooms, CancellationToken ct)
    {
        var root = await _db.Sections.FirstOrDefaultAsync(s => s.Id == rootId, ct);
        if (root is null || !visibleRooms.Contains(root.RoomId)) return [];

        // Walk down level by level. The domain caps folders at two levels, but nothing here assumes that.
        var all = await _db.Sections.Where(s => s.RoomId == root.RoomId).Select(s => new { s.Id, s.ParentId }).ToListAsync(ct);
        var ids = new List<Guid> { rootId };
        for (var frontier = new List<Guid> { rootId }; frontier.Count > 0;)
        {
            var next = all.Where(s => s.ParentId != null && frontier.Contains(s.ParentId.Value)).Select(s => s.Id).ToList();
            ids.AddRange(next);
            frontier = next;
        }
        return ids;
    }

    /// <summary>Folders whose name matches, in rooms the caller can see. Plain EF (not the raw-SQL engine) so it
    /// translates on every provider - and so the whole of this controller stays unit-testable without Postgres.
    /// <c>ToLower().Contains</c> rather than <c>EF.Functions.ILike</c>, which is Npgsql-only.</summary>
    private async Task<IReadOnlyList<FolderHitDto>> FoldersAsync(string query, HashSet<Guid> visibleRooms, CancellationToken ct)
    {
        var needle = query.ToLowerInvariant();
        var rooms = await _db.Rooms.Where(r => visibleRooms.Contains(r.Id))
            .Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        var candidates = await _db.Sections
            .Where(s => visibleRooms.Contains(s.RoomId) && s.Name.ToLower().Contains(needle))
            .Select(s => new { s.Id, s.Name, s.ParentId, s.RoomId })
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        // Names for the breadcrumb, and the counts, come from the matched folders' rooms only.
        var roomIds = candidates.Select(c => c.RoomId).Distinct().ToList();
        var siblings = await _db.Sections.Where(s => roomIds.Contains(s.RoomId))
            .Select(s => new { s.Id, s.Name, s.ParentId }).ToListAsync(ct);
        var byId = siblings.ToDictionary(s => s.Id);
        var childCounts = await _db.RoomRecordings.Where(p => p.SectionId != null)
            .GroupBy(p => p.SectionId!.Value)
            .Select(g => new { SectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SectionId, x => x.Count, ct);

        var result = new List<FolderHitDto>();
        foreach (var c in candidates)
        {
            var crumb = new List<string>();
            for (var p = c.ParentId; p is Guid pid && byId.TryGetValue(pid, out var parent);)
            {
                crumb.Insert(0, parent.Name);
                p = parent.ParentId;
                if (crumb.Count > 32) break; // a parent cycle is not schema-enforced; don't spin
            }

            // Everything underneath, matching the drill-in list's promise.
            var total = 0;
            var frontier = new List<Guid> { c.Id };
            while (frontier.Count > 0)
            {
                total += frontier.Sum(id => childCounts.TryGetValue(id, out var n) ? n : 0);
                frontier = siblings.Where(s => s.ParentId != null && frontier.Contains(s.ParentId.Value))
                    .Select(s => s.Id).ToList();
            }

            result.Add(new FolderHitDto(
                c.Id, c.Name, c.ParentId, c.RoomId, rooms.TryGetValue(c.RoomId, out var rn) ? rn : "",
                crumb, total));
        }
        return result;
    }

    /// <summary>Collapse the engine's per-segment hits to one row per recording (its best passage), attach where
    /// each recording lives, and apply the date window.</summary>
    private async Task<IReadOnlyList<RecordingSearchHitDto>> ProjectAsync(
        IReadOnlyList<TranscriptHit> hits, HashSet<Guid> visibleRooms,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var windowed = hits
            .Where(h => (from is null || h.RecordingCreatedAt >= from) && (to is null || h.RecordingCreatedAt <= to))
            .ToList();
        if (windowed.Count == 0) return [];

        var best = windowed
            .GroupBy(h => h.RecordingId)
            .Select(g => g.OrderByDescending(h => h.Similarity).First())
            .ToList();

        var ids = best.Select(b => b.RecordingId).ToList();
        var durations = await _db.Recordings.Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.DurationMs }).ToDictionaryAsync(r => r.Id, r => r.DurationMs, ct);

        // Where a recording "lives" is a per-room placement, and a recording shared into several rooms has
        // several. Only rooms the caller can see are candidates - a placement in someone else's room is not
        // theirs to read, so its folder name can never leak. Personal first, then by room name: deterministic,
        // and it always names a room the caller can actually reach.
        var placements = await _db.RoomRecordings
            .Where(p => ids.Contains(p.RecordingId) && visibleRooms.Contains(p.RoomId))
            .Join(_db.Rooms, p => p.RoomId, r => r.Id,
                (p, r) => new { p.RecordingId, p.SectionId, p.RoomId, r.Kind, RoomName = r.Name })
            .ToListAsync(ct);
        var placementFor = placements
            .GroupBy(p => p.RecordingId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(p => p.Kind == RoomKind.Personal).ThenBy(p => p.RoomName).First());

        var byId = (await _db.Sections.Select(s => new { s.Id, s.Name, s.ParentId, s.RoomId }).ToListAsync(ct))
            .ToDictionary(s => s.Id);

        return best.Select(h =>
        {
            placementFor.TryGetValue(h.RecordingId, out var placement);

            // Belt and braces: a placement's folder must belong to that placement's own room. Folders are
            // per-room, so this should always hold - but reading a folder name that isn't from the room we
            // resolved would leak a name out of a room the caller may not be in, so verify rather than trust.
            var sectionId = placement?.SectionId is Guid psid
                && byId.TryGetValue(psid, out var placed)
                && placed.RoomId == placement.RoomId
                    ? psid
                    : (Guid?)null;

            var crumb = new List<string>();
            for (var p = sectionId; p is Guid pid && byId.TryGetValue(pid, out var node);)
            {
                crumb.Insert(0, node.Name);
                p = node.ParentId;
                if (crumb.Count > 32) break; // a parent cycle is not schema-enforced; don't spin
            }

            return new RecordingSearchHitDto(
                h.RecordingId, h.RecordingName, h.RecordingCreatedAt,
                durations.TryGetValue(h.RecordingId, out var d) ? d : 0,
                sectionId,
                sectionId is Guid sid2 && byId.TryGetValue(sid2, out var sec) ? sec.Name : null,
                crumb, h.Text, h.StartMs, h.SpeakerName, h.Similarity);
        }).ToList();
    }
}
