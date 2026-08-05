using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>The parent link of one folder, flattened for the pure tree functions below. Deliberately not the
/// <c>Section</c> entity: these functions are provider-agnostic and must stay cheap to unit-test.</summary>
public readonly record struct SectionLink(Guid Id, Guid? ParentId);

/// <summary>The single place that knows how folders nest. Every "the recordings in this folder" query used to
/// hand-roll <c>ParentId == sectionId</c>, which only ever saw DIRECT children - correct while the hierarchy was
/// capped at two levels, silently wrong the moment it was not. The walk is level-by-level rather than a recursive
/// CTE so it translates on every EF provider, including the in-memory one the unit tests use.</summary>
public static class SectionTree
{
    /// <summary>A folder and every folder beneath it, root first. The root is always included, even if it is not
    /// in <paramref name="sections"/> (a folder deleted mid-request reads as "just itself", never as "everything").
    /// A <c>ParentId</c> cycle is not schema-enforced, so the visited set is what stops this spinning.</summary>
    public static List<Guid> Subtree(IReadOnlyCollection<SectionLink> sections, Guid rootId)
    {
        var ids = new List<Guid> { rootId };
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };

        while (frontier.Count > 0)
        {
            var next = new List<Guid>();
            foreach (var s in sections)
            {
                if (s.ParentId is not Guid p || !frontier.Contains(p)) continue;
                if (!seen.Add(s.Id)) continue;
                ids.Add(s.Id);
                next.Add(s.Id);
            }
            frontier = next;
        }

        return ids;
    }

    /// <summary>Every folder link in one room. A room's folder list is small (tens of rows) and already the unit
    /// the nav loads, so pulling it whole and walking it in memory is cheaper than a round trip per level.</summary>
    public static Task<List<SectionLink>> LinksAsync(DiarizDbContext db, Guid roomId, CancellationToken ct) =>
        db.Sections.Where(s => s.RoomId == roomId)
            .Select(s => new SectionLink(s.Id, s.ParentId))
            .ToListAsync(ct);

    /// <summary>The folder plus every folder beneath it, within one room - the set a recording's placement
    /// <c>SectionId</c> must be in to count as "included" in that folder.</summary>
    public static async Task<List<Guid>> SubtreeIdsAsync(
        DiarizDbContext db, Guid roomId, Guid rootId, CancellationToken ct) =>
        Subtree(await LinksAsync(db, roomId, ct), rootId);
}
