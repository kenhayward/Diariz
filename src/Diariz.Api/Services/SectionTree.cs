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

    /// <summary>How deep folders may nest. Top-level is depth 1, so 8 means eight levels of folder. A guardrail,
    /// not a design constraint: it bounds the breadcrumb and the folder pickers, and turns a cycle that somehow
    /// evaded the descendant check into a rejected request rather than a hung one.</summary>
    public const int MaxDepth = 8;

    /// <summary>The folder's level, counting top-level as 1. Zero for an id that is not in the list. The visited
    /// set bounds a <c>ParentId</c> cycle, which the schema does not prevent.</summary>
    public static int Depth(IReadOnlyCollection<SectionLink> sections, Guid id)
    {
        var byId = sections.ToDictionary(s => s.Id);
        if (!byId.TryGetValue(id, out var current)) return 0;

        var depth = 1;
        var seen = new HashSet<Guid> { id };
        while (current.ParentId is Guid parentId && seen.Add(parentId) && byId.TryGetValue(parentId, out current))
            depth++;
        return depth;
    }

    /// <summary>How many levels the subtree rooted here spans, counting the root as 1. Moving a folder moves its
    /// whole branch, so this is what a reparent has to add to the target's depth. Note: returns 1 for an unknown
    /// <paramref name="rootId"/> (unlike <see cref="Depth"/>, which returns 0); an unknown id still occupies one
    /// level as far as a caller composing <c>Depth(target) + Height(moved)</c> is concerned. This is safe because
    /// callers validate existence upstream (<c>SectionsController.Reorder</c> 404s on ids not in the room).</summary>
    public static int Height(IReadOnlyCollection<SectionLink> sections, Guid rootId)
    {
        var subtree = Subtree(sections, rootId).ToHashSet();
        var rootDepth = Depth(sections, rootId);
        if (rootDepth == 0) return 1; // unknown root: it is its own single level

        var deepest = rootDepth;
        foreach (var id in subtree)
        {
            var d = Depth(sections, id);
            if (d > deepest) deepest = d;
        }
        return deepest - rootDepth + 1;
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
