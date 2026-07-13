using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Resolves the display "origin" of formula results for the runs-list icon, batched (no N+1):
/// Diariz/Platform formulas are "official" (no person); Personal formulas attribute to the owner; a result
/// whose formula was deleted (FormulaId SET NULL) attributes to its creator. Shared by the results and run
/// endpoints so the icon is consistent everywhere.</summary>
public static class FormulaResultOrigins
{
    public static Task<IReadOnlyDictionary<Guid, FormulaResultOriginDto>> ResolveAsync(
        DiarizDbContext db, IReadOnlyList<FormulaResult> results, CancellationToken ct = default) =>
        ResolveAsync(db, results.Select(r => (r.Id, r.FormulaId, r.CreatedByUserId)), ct);

    /// <summary>The core resolver over a lightweight projection - each result's id, its (nullable) formula id, and
    /// its (nullable) creator - so both recording (<see cref="FormulaResult"/>) and folder
    /// (<c>SectionFormulaResult</c>) results share one batched lookup. Callers pass the entity-specific overload
    /// (above) or this tuple form directly.</summary>
    public static async Task<IReadOnlyDictionary<Guid, FormulaResultOriginDto>> ResolveAsync(
        DiarizDbContext db, IEnumerable<(Guid ResultId, Guid? FormulaId, Guid? CreatedByUserId)> results,
        CancellationToken ct = default)
    {
        var list = results.ToList();
        var formulaIds = list.Where(r => r.FormulaId != null).Select(r => r.FormulaId!.Value).Distinct().ToList();
        var formulas = (await db.Formulas.Where(f => formulaIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Scope, f.OwnerUserId }).ToListAsync(ct))
            .ToDictionary(f => f.Id, f => (f.Scope, f.OwnerUserId));

        var personIds = new HashSet<Guid>();
        foreach (var r in list)
        {
            if (r.FormulaId is Guid fid && formulas.TryGetValue(fid, out var f))
            {
                if (f.Scope == FormulaScope.Personal && f.OwnerUserId is Guid oid) personIds.Add(oid);
            }
            else if (r.CreatedByUserId is Guid cid) personIds.Add(cid);
        }

        var people = (await db.Users.Where(u => personIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email, u.PictureUrl }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => (u.FullName, u.Email, u.PictureUrl));

        var origins = new Dictionary<Guid, FormulaResultOriginDto>(list.Count);
        foreach (var r in list)
        {
            FormulaScope? scope = null;
            Guid? personId = null;
            if (r.FormulaId is Guid fid && formulas.TryGetValue(fid, out var f))
            {
                scope = f.Scope;
                personId = f.Scope == FormulaScope.Personal ? f.OwnerUserId : null;
            }
            else
            {
                personId = r.CreatedByUserId; // formula deleted/missing -> attribute to the creator
            }
            origins[r.ResultId] = Build(scope, personId, people);
        }
        return origins;
    }

    private static FormulaResultOriginDto Build(
        FormulaScope? scope, Guid? personId,
        IReadOnlyDictionary<Guid, (string? FullName, string? Email, string? PictureUrl)> people)
    {
        var kind = scope switch
        {
            FormulaScope.Diariz => "diariz",
            FormulaScope.Platform => "platform",
            _ => "personal", // Personal, or a deleted formula (scope null)
        };
        if (scope is FormulaScope.Diariz or FormulaScope.Platform)
            return new(kind, null, null);
        if (personId is Guid id && people.TryGetValue(id, out var p))
            return new(kind, string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName, p.PictureUrl);
        return new(kind, null, null);
    }
}
