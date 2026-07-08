using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Backfill that enqueues a tag-extraction job for every recording never tagged
/// (<c>TagsExtractedAt == null</c>), targeting each recording's latest transcription, so a pre-feature
/// library gets its tag cloud. Idempotent: the processor sets the marker even on a zero-tag result, so an
/// already-processed recording is never re-enqueued; only owners without an LLM stay pending (their marker
/// stays null) and are picked up by a later run. Mirrors <see cref="EmbeddingBackfill"/>.</summary>
public static class TagBackfill
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, IJobQueue queue, ILogger logger, CancellationToken ct = default)
    {
        // Latest (max-version) transcription per never-tagged recording. Uses a Max aggregate + join
        // rather than OrderBy/First-in-projection so it behaves under the in-memory test provider too.
        var maxByRec = db.Transcriptions
            .GroupBy(t => t.RecordingId)
            .Select(g => new { RecordingId = g.Key, Version = g.Max(t => t.Version) });

        var pending = await (
            from t in db.Transcriptions
            join m in maxByRec on new { t.RecordingId, t.Version } equals new { m.RecordingId, m.Version }
            join r in db.Recordings on t.RecordingId equals r.Id
            where r.TagsExtractedAt == null
            select new { t.RecordingId, TranscriptionId = t.Id }).ToListAsync(ct);

        foreach (var p in pending)
        {
            if (ct.IsCancellationRequested) break;
            await queue.EnqueueTagsAsync(new TagsJob(p.RecordingId, p.TranscriptionId), ct);
        }

        logger.LogInformation("Tag backfill: enqueued {Count} recording(s) for tag extraction.", pending.Count);
        return pending.Count;
    }
}

/// <summary>Runs <see cref="TagBackfill"/> once shortly after startup, off the critical boot path.
/// Skips entirely when no summarisation endpoint is configured server-wide (tags use the chat LLM) - owners
/// with a per-user-only endpoint are covered by the Platform Admin's manual "Backfill tags" run or by their
/// next (re)transcription. Mirrors <see cref="EmbeddingBackfillService"/>.</summary>
public class TagBackfillService(
    IServiceProvider services, IOptions<SummarizationOptions> summary,
    ILogger<TagBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(summary.Value.ApiBase)) return;

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await TagBackfill.RunAsync(db, queue, logger, stoppingToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Tag backfill failed.");
        }
    }
}
