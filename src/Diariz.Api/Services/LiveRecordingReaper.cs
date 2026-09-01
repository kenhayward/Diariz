using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>
/// Collects live captures whose client disappeared - a closed lid, a killed tab, a crashed browser.
/// Without this they sit at <see cref="RecordingStatus.Live"/> forever, holding a quota charge and
/// showing the user a recording that will never finish.
/// <para>
/// Note what it buys beyond tidiness: today the same event loses the <b>entire</b> meeting, because
/// nothing reaches the server until Stop. After this, whatever arrived is kept.
/// </para>
/// </summary>
public static class LiveRecordingSweep
{
    /// <summary>Whether a recording should be collected. Pure and total: the caller passes the newest
    /// chunk's arrival time, or the recording's own creation time when no chunk ever came, so there is
    /// no null case to reason about.
    /// <para>
    /// <see cref="RecordingStatus.Merging"/> is deliberately excluded: finalise is already in flight,
    /// and reaping it would race the worker callback - the one failure here that could actually corrupt
    /// a recording rather than merely inconvenience someone.
    /// </para></summary>
    public static bool IsAbandoned(
        RecordingStatus status, DateTimeOffset lastActivity, DateTimeOffset now, TimeSpan abandonAfter) =>
        status == RecordingStatus.Live && now - lastActivity > abandonAfter;

    /// <summary>One pass. A per-item failure is logged and skipped rather than aborting the sweep, so
    /// one wedged recording cannot stop every other one being collected.</summary>
    public static async Task RunAsync(
        DiarizDbContext db, IAudioStorage storage, IJobQueue queue, IHubContext<TranscriptionHub> hub,
        DateTimeOffset now, TimeSpan abandonAfter, ILogger logger, CancellationToken ct = default)
    {
        var cutoff = now - abandonAfter;

        // Project rather than Include: the decision needs one timestamp per recording, not the chunks
        // themselves, and a live capture can hold hundreds of them.
        var candidates = await db.Recordings
            .Where(r => r.Status == RecordingStatus.Live)
            .Select(r => new
            {
                r.Id,
                r.UserId,
                r.Status,
                LastActivity = db.RecordingChunks
                    .Where(c => c.RecordingId == r.Id)
                    .Max(c => (DateTimeOffset?)c.ReceivedAt) ?? r.CreatedAt,
            })
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (!IsAbandoned(candidate.Status, candidate.LastActivity, now, abandonAfter)) continue;

            try
            {
                var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == candidate.Id, ct);
                if (rec is null || rec.Status != RecordingStatus.Live) continue; // finalised under us

                var chunks = await db.RecordingChunks
                    .Where(c => c.RecordingId == rec.Id)
                    .OrderBy(c => c.Sequence)
                    .ToListAsync(ct);

                if (chunks.Count == 0)
                {
                    // Nothing was ever captured, so there is no audio to rescue.
                    db.Recordings.Remove(rec);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Reaped empty live recording {RecordingId} (no chunks since {Cutoff:o})",
                        rec.Id, cutoff);
                    continue;
                }

                // Unlike the interactive endpoint, a gap is accepted here. Nobody is left to retry the
                // missing chunk, so refusing would strand the recording in Live forever - which is the
                // opposite of what this exists to do. The audio that arrived is still the user's.
                rec.Status = RecordingStatus.Merging;
                var outputKey = $"{rec.UserId}/{rec.Id}-live-{Guid.NewGuid():N}.webm";
                await db.SaveChangesAsync(ct);

                try
                {
                    await queue.EnqueueAudioMergeAsync(new AudioMergeJob(
                        rec.Id, chunks.Select(c => c.BlobKey).ToList(), outputKey, [], Kind: "live-chunks"), ct);
                }
                catch
                {
                    // Merging is already committed, and the job that clears it is the one that just
                    // failed to queue. Leaving it would strand the recording in exactly the state this
                    // sweep exists to clear - and worse, IsAbandoned excludes Merging, so no later pass
                    // would ever pick it up again. Put it back so the next sweep retries.
                    rec.Status = RecordingStatus.Live;
                    await db.SaveChangesAsync(ct);
                    throw;
                }
                await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());

                logger.LogInformation(
                    "Finalised abandoned live recording {RecordingId} from {ChunkCount} chunks",
                    rec.Id, chunks.Count);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not reap live recording {RecordingId}", candidate.Id);
            }
        }
    }
}

/// <summary>Runs <see cref="LiveRecordingSweep"/> on an interval. Abandonment is not a nightly
/// concern - a stranded capture holds a quota charge and looks broken to its owner - so this ticks
/// frequently rather than at a scheduled time of day like the audio-retention job.</summary>
public class LiveRecordingReaper(
    IServiceProvider services, IOptions<LiveCaptureOptions> options, ILogger<LiveRecordingReaper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var interval = TimeSpan.FromMinutes(Math.Max(1, opts.ReaperIntervalMinutes));
        var abandonAfter = TimeSpan.FromMinutes(Math.Max(1, opts.AbandonAfterMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = services.CreateScope();
                var sp = scope.ServiceProvider;
                await LiveRecordingSweep.RunAsync(
                    sp.GetRequiredService<DiarizDbContext>(),
                    sp.GetRequiredService<IAudioStorage>(),
                    sp.GetRequiredService<IJobQueue>(),
                    sp.GetRequiredService<IHubContext<TranscriptionHub>>(),
                    DateTimeOffset.UtcNow, abandonAfter, logger, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Live recording sweep failed.");
            }
        }
    }
}
