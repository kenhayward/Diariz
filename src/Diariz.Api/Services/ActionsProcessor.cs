using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>
/// Processes a single action-extraction job as part of the pipeline: builds the transcript, calls the LLM,
/// and persists the recording's <see cref="RecordingAction"/>s. Runs in parallel with the summary + minutes
/// and deliberately does NOT change <c>Recording.Status</c> (so it never races the summary's status
/// transitions). Skips when actions have already been extracted (or the user added their own) so the
/// automatic run never clobbers manual edits — an explicit re-extract (the controller) still replaces them.
/// On success it notifies the owning user so the browser refetches and shows the actions. Pulled out of the
/// BackgroundService so it can be unit-tested with a fake client + in-memory DbContext. Mirrors
/// <see cref="MeetingMinutesProcessor"/>.
/// </summary>
public static class ActionsProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, IActionsClient client, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, ActionsJob job, string template, ILogger logger,
        CancellationToken ct = default)
    {
        var rec = await db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == job.RecordingId, ct);
        if (rec is null) return; // recording deleted before the job ran — nothing to do.

        // Auto-extract only once: a set ActionsExtractedAt means extraction ran (or the user added an action),
        // so the pipeline must not overwrite it. Explicit re-extraction goes through the controller.
        if (rec.ActionsExtractedAt is not null) return;

        try
        {
            var transcription = await db.Transcriptions
                .Include(t => t.Segments)
                .FirstOrDefaultAsync(t => t.Id == job.TranscriptionId, ct)
                ?? throw new InvalidOperationException("Transcription not found.");

            var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
            var segs = transcription.Segments
                .OrderBy(s => s.Ordinal)
                .Select(s => new SegmentDto(
                    s.Id, s.SpeakerLabel,
                    names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                    s.StartMs, s.EndMs, s.Original, s.Revised))
                .ToList();
            if (segs.Count == 0) return; // nothing to extract from.

            // Use the recording owner's effective config (their endpoint/key/model, else server defaults).
            var cfg = await resolver.ResolveAsync(rec.UserId, ct);
            if (!cfg.Enabled) return; // no LLM configured — leave actions unextracted (a re-run can retry).

            var extracted = await client.ExtractAsync(cfg, segs, template, rec.CreatedAt, ct);

            // Seed the (empty) action list with the extraction; RemoveRange is a no-op defensively.
            db.RecordingActions.RemoveRange(rec.Actions);
            var ordinal = 0;
            db.RecordingActions.AddRange(extracted.Select(e => new RecordingAction
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Text = e.Text,
                Actor = e.Actor,
                Deadline = e.Deadline,
                Ordinal = ordinal++,
            }));
            rec.ActionsExtractedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            // Nudge the browser to refetch (status is unchanged — actions don't own the recording status).
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
        }
        catch (Exception ex)
        {
            // Don't mark the recording Failed — the transcript (and any summary/minutes) are still valid; only
            // action extraction didn't run. Log and leave the status (and ActionsExtractedAt) untouched.
            logger.LogError(ex, "Action extraction failed for recording {RecordingId}", rec.Id);
        }
    }
}
