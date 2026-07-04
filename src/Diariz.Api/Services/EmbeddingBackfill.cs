using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>One-time backfill that enqueues an embedding job for every recording whose latest transcription
/// has no chunks yet, so an existing library gets indexed once an embeddings endpoint is configured.
/// Idempotent: a recording that already has chunks is skipped, and jobs re-run harmlessly (the processor
/// replaces chunks wholesale).</summary>
public static class EmbeddingBackfill
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, IJobQueue queue, ILogger logger, CancellationToken ct = default)
    {
        // Latest (max-version) transcription per recording that has no chunks. Uses a Max aggregate + join
        // rather than OrderBy/First-in-projection so it behaves under the in-memory test provider too.
        var maxByRec = db.Transcriptions
            .GroupBy(t => t.RecordingId)
            .Select(g => new { RecordingId = g.Key, Version = g.Max(t => t.Version) });

        var pending = await (
            from t in db.Transcriptions
            join m in maxByRec on new { t.RecordingId, t.Version } equals new { m.RecordingId, m.Version }
            where !db.TranscriptChunks.Any(c => c.TranscriptionId == t.Id)
            select new { t.RecordingId, TranscriptionId = t.Id }).ToListAsync(ct);

        foreach (var p in pending)
        {
            if (ct.IsCancellationRequested) break;
            await queue.EnqueueEmbeddingAsync(new EmbeddingJob(p.RecordingId, p.TranscriptionId), ct);
        }

        logger.LogInformation("Embedding backfill: enqueued {Count} recording(s) for indexing.", pending.Count);
        return pending.Count;
    }
}

/// <summary>Runs <see cref="EmbeddingBackfill"/> once shortly after startup, off the critical boot path.
/// Skips entirely when no embeddings endpoint is configured server-wide (embedding or summarisation) - a
/// per-user-only endpoint still gets indexed on the next (re)transcription, so there's nothing to backfill.</summary>
public class EmbeddingBackfillService(
    IServiceProvider services, IOptions<EmbeddingOptions> embedding, IOptions<SummarizationOptions> summary,
    ILogger<EmbeddingBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverHasEndpoint = !string.IsNullOrWhiteSpace(embedding.Value.ApiBase)
            || !string.IsNullOrWhiteSpace(summary.Value.ApiBase);
        if (!serverHasEndpoint) return;

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await EmbeddingBackfill.RunAsync(db, queue, logger, stoppingToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Embedding backfill failed.");
        }
    }
}
