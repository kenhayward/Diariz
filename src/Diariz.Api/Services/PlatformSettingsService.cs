using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Reads (and lazily creates) the singleton <see cref="PlatformSettings"/> row. The returned
/// entity is tracked by the request's <see cref="DiarizDbContext"/>, so callers can mutate + SaveChanges.</summary>
public interface IPlatformSettingsService
{
    Task<PlatformSettings> GetAsync(CancellationToken ct = default);
}

public class PlatformSettingsService(DiarizDbContext db) : IPlatformSettingsService
{
    public async Task<PlatformSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);
        if (row is null)
        {
            // The row is normally seeded via migration; create it defensively (covers the in-memory
            // unit provider, which doesn't apply HasData seeds).
            row = new PlatformSettings { Id = PlatformSettings.SingletonId };
            db.PlatformSettings.Add(row);
            await db.SaveChangesAsync(ct);
        }
        return row;
    }
}
