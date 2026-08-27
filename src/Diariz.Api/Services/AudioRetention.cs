using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Pure scheduling helper for the nightly audio-retention job.</summary>
public static class AudioRetentionSchedule
{
    /// <summary>The next occurrence of <paramref name="timeOfDay"/> strictly after <paramref name="now"/>,
    /// in the same offset as <paramref name="now"/> (i.e. "server-local" time). If today's occurrence has
    /// already passed (or is exactly now), returns tomorrow's.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly timeOfDay)
    {
        var today = new DateTimeOffset(
            now.Year, now.Month, now.Day, timeOfDay.Hour, timeOfDay.Minute, timeOfDay.Second, now.Offset);
        return today > now ? today : today.AddDays(1);
    }
}

/// <summary>Deletes the audio blob of recordings past the retention window, keeping the transcript and
/// metadata (mirrors <see cref="Controllers.RecordingsController.DeleteAudio"/>). Only fully transcribed,
/// unprotected recordings that still have audio are touched. Idempotent and resilient: a per-item failure is
/// logged and skipped without aborting the sweep.</summary>
public static class AudioRetentionSweep
{
    // Statuses that guarantee a completed transcript exists, so removing the audio loses nothing.
    private static readonly RecordingStatus[] Transcribed =
    [
        RecordingStatus.Transcribed, RecordingStatus.Summarized, RecordingStatus.Summarizing,
    ];

    /// <summary>Whether a recording's status makes it eligible for audio deletion (a completed transcript
    /// exists). Shared with the detail projection so the "will be deleted" hint matches the sweep exactly.</summary>
    public static bool IsTranscribedStatus(RecordingStatus status) => Transcribed.Contains(status);

    /// <summary>Whether the nightly sweep would delete this recording's audio.
    ///
    /// <para>One predicate, called by the sweep and by the detail page's "will be deleted on" hint. They were
    /// two expressions of the same rule, which is how a hint comes to promise a deletion that will not
    /// happen.</para>
    ///
    /// <para><paramref name="trainsAVoiceprint"/> is the exemption: <b>audio a voiceprint was enrolled from
    /// is kept</b>. Whether a recording really is the right person can only be settled by ear, so deleting it
    /// makes that permanently unanswerable while the embedding it produced goes on being used. Found live:
    /// 74 of 156 training samples were already past the window, so nearly half the review queue could not be
    /// listened to.</para>
    ///
    /// <para>It covers the <b>automatic</b> policy only. Deleting audio by hand is an explicit act and stays
    /// available - the objection is to a background job silently destroying the evidence, not to a person
    /// deciding they no longer want it.</para></summary>
    public static bool WouldDelete(
        RecordingStatus status, bool hasAudio, bool isProtected, bool trainsAVoiceprint) =>
        hasAudio && !isProtected && !trainsAVoiceprint && IsTranscribedStatus(status);

    public static async Task<int> RunAsync(
        DiarizDbContext db, IAudioStorage storage, DateTimeOffset nowUtc, int retentionDays,
        ILogger logger, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddDays(-retentionDays);
        var candidates = await db.Recordings
            .Where(r => r.AudioDeletedAt == null
                && r.AudioProtectedAt == null
                && r.CreatedAt < cutoff
                && Transcribed.Contains(r.Status)
                // Audio a voiceprint was enrolled from is kept - see WouldDelete. Derived from the samples
                // rather than a flag on the recording, so dropping the last sample lets it rejoin the policy
                // without anything having to remember to clear a marker.
                && !db.VoiceSamples.Any(v => v.RecordingId == r.Id && v.ExcludedAt == null))
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var deleted = 0;
        foreach (var rec in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await storage.DeleteAsync(rec.BlobKey, ct);
                rec.AudioDeletedAt = nowUtc;
                rec.SizeBytes = 0; // stop counting toward the owner's quota (UsedBytes = SUM(SizeBytes))
                deleted++;
            }
            catch (Exception e)
            {
                // A single unreachable/undeletable blob shouldn't abort the whole sweep.
                logger.LogWarning(e, "Audio retention: could not delete blob {Key}", rec.BlobKey);
            }
        }

        if (deleted > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation("Audio retention: deleted audio for {Deleted}/{Total} recording(s) older than {Days}d.",
            deleted, candidates.Count, retentionDays);
        return deleted;
    }
}

/// <summary>Runs <see cref="AudioRetentionSweep"/> once per day at the Platform-Administrator-configured
/// server-local time, when enabled. Opens its own DI scope per run because the host is a singleton (like
/// <see cref="StorageBackfillService"/>). The schedule/enablement are re-read each cycle, so settings changes
/// take effect from the next run.</summary>
public class AudioRetentionWorker(IServiceProvider services, ILogger<AudioRetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeOnly timeOfDay;
            try
            {
                using var scope = services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>();
                timeOfDay = (await settings.GetAsync(stoppingToken)).AudioDeletionTimeOfDay;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Audio retention: could not read settings; retrying in 1h.");
                await Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            var next = AudioRetentionSchedule.NextRun(DateTimeOffset.Now, timeOfDay);
            if (!await Delay(next - DateTimeOffset.Now, stoppingToken)) return;

            try
            {
                using var scope = services.CreateScope();
                var settings = await scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>()
                    .GetAsync(stoppingToken);
                if (!settings.AutoDeleteAudioEnabled) continue; // opted out - wait for the next day

                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<IAudioStorage>();
                await AudioRetentionSweep.RunAsync(
                    db, storage, DateTimeOffset.UtcNow, settings.AudioRetentionDays, logger, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Audio retention sweep failed.");
            }
        }
    }

    /// <summary>Cancellation-aware delay; returns false if the wait was cancelled (host shutting down).</summary>
    private static async Task<bool> Delay(TimeSpan delay, CancellationToken ct)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
