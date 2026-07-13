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
    public static async Task<IReadOnlyDictionary<Guid, FormulaResultOriginDto>> ResolveAsync(
        DiarizDbContext db, IReadOnlyList<FormulaResult> results, CancellationToken ct = default)
    {
        var formulaIds = results.Where(r => r.FormulaId != null).Select(r => r.FormulaId!.Value).Distinct().ToList();
        var formulas = (await db.Formulas.Where(f => formulaIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Scope, f.OwnerUserId }).ToListAsync(ct))
            .ToDictionary(f => f.Id, f => (f.Scope, f.OwnerUserId));

        var personIds = new HashSet<Guid>();
        foreach (var r in results)
        {
            if (r.FormulaId is Guid fid && formulas.TryGetValue(fid, out var f))
            {
                if (f.Scope == FormulaScope.Personal && f.OwnerUserId is Guid oid) personIds.Add(oid);
            }
            else if (r.CreatedByUserId is Guid cid) personIds.Add(cid);
        }

        var people = (await db.Users.Where(u => personIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => (u.FullName, u.Email));
        var pictures = (await db.Users.Where(u => personIds.Contains(u.Id))
                .Select(u => new { u.Id, u.PictureUrl }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u.PictureUrl);

        var origins = new Dictionary<Guid, FormulaResultOriginDto>(results.Count);
        foreach (var r in results)
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
            origins[r.Id] = Build(scope, personId, people, pictures);
        }
        return origins;
    }

    private static FormulaResultOriginDto Build(
        FormulaScope? scope, Guid? personId,
        IReadOnlyDictionary<Guid, (string? FullName, string? Email)> people,
        IReadOnlyDictionary<Guid, string?> pictures)
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
            return new(kind, string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
                pictures.TryGetValue(id, out var pic) ? pic : null);
        return new(kind, null, null);
    }
}
