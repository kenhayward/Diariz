using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Whether a recording is transcribed live, under the platform's switch. Pure.
///
/// <para>Off refuses everything at once. On admits only recordings created at or after the moment it came
/// back on: a meeting that started while it was off stays off until it ends, rather than resuming with an
/// unexplained gap in its transcript and a panel that has to rebuild what it had just taken away.</para></summary>
public static class LiveTranscriptionGate
{
    public static bool Allows(PlatformSettings? settings, DateTimeOffset recordingCreatedAt) =>
        settings is null
        || (settings.LiveTranscriptionEnabled
            && (settings.LiveTranscriptionChangedAt is not { } changedAt || recordingCreatedAt >= changedAt));
}

/// <summary>Flips the platform's live-transcription switch, with what has to happen at that moment.
/// The caller saves.</summary>
public static class LiveTranscriptionSwitch
{
    public static async Task ApplyAsync(
        DiarizDbContext db, IJobQueue queue, PlatformSettings settings, bool enabled, DateTimeOffset now,
        ILogger? logger = null, CancellationToken ct = default)
    {
        // The settings page saves every field together, so an unrelated save arrives here too. It must not
        // move "off since", nor make meetings already running count as having started before the switch.
        if (settings.LiveTranscriptionEnabled == enabled) return;

        settings.LiveTranscriptionEnabled = enabled;
        settings.LiveTranscriptionChangedAt = now;

        if (!enabled)
        {
            // Nothing outstanding will be transcribed now. Left unsettled, those chunks would read as a
            // backlog the moment the switch came back on and pause the next meeting (issue #758).
            var live = db.Recordings.Where(r => r.Status == RecordingStatus.Live).Select(r => r.Id);
            var outstanding = await db.RecordingChunks
                .Where(c => c.SettledAt == null && live.Contains(c.RecordingId))
                .ToListAsync(ct);
            foreach (var chunk in outstanding) chunk.SettledAt = now;
        }

        try
        {
            await queue.SetLiveTranscriptionEnabledAsync(enabled, ct);
        }
        catch (Exception e)
        {
            // The database is the record, and the API refuses new chunks on its own say-so. A failed write
            // here costs only the dropping of chunks already queued and the live worker's unload.
            logger?.LogWarning(e, "Could not tell the workers live transcription is now {State}",
                enabled ? "on" : "off");
        }
    }
}
