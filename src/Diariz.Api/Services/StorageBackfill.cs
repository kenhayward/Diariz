using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>One-time backfill of <c>Recording.SizeBytes</c> for legacy rows (size 0, created before
/// sizes were tracked) by HEAD-ing each stored blob. Idempotent: only rows with size 0 and a blob key
/// are touched, so it no-ops once every recording has a size.</summary>
public static class StorageBackfill
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, IAudioStorage storage, ILogger logger, CancellationToken ct = default)
    {
        var pending = await db.Recordings
            .Where(r => r.SizeBytes == 0 && r.BlobKey != "")
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var updated = 0;
        foreach (var rec in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var size = await storage.GetSizeAsync(rec.BlobKey, ct);
                if (size is > 0)
                {
                    rec.SizeBytes = size.Value;
                    updated++;
                }
            }
            catch (Exception e)
            {
                // A missing/unreadable blob shouldn't abort the whole backfill.
                logger.LogWarning(e, "Storage backfill: could not size blob {Key}", rec.BlobKey);
            }
        }

        if (updated > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation("Storage backfill: set size on {Updated}/{Total} legacy recording(s).",
            updated, pending.Count);
        return updated;
    }
}

/// <summary>Runs <see cref="StorageBackfill"/> once shortly after startup, off the critical boot path
/// (so it doesn't delay readiness). Opens its own DI scope because the host is a singleton.</summary>
public class StorageBackfillService(IServiceProvider services, ILogger<StorageBackfillService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IAudioStorage>();
            await StorageBackfill.RunAsync(db, storage, logger, stoppingToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Storage backfill failed.");
        }
    }
}
