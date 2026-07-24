using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Diariz.Domain.Entities;
using MinutesEntity = Diariz.Domain.Entities.MeetingMinutes;

namespace Diariz.Api.Services;

/// <summary>
/// Processes a single meeting-minutes job: builds the transcript, calls the LLM, and persists the
/// <see cref="MinutesEntity"/> (Markdown). Deliberately does NOT change <c>Recording.Status</c> — minutes
/// generate in parallel with the summary, so touching status would race the summary's
/// Summarizing→Summarized transition. On success it notifies the owning user (with the recording's current
/// status) so the browser refetches and shows the minutes. Pulled out of the BackgroundService so it can be
/// unit-tested with a fake client + in-memory DbContext.
/// </summary>
public static class MeetingMinutesProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, IMeetingTypeMinutesGenerator generator, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, IJobQueue queue, MeetingMinutesJob job, int charBudget, ILogger logger,
        IWebhookPublisher webhooks, string publicUrl,
        CancellationToken ct = default)
    {
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == job.RecordingId, ct);
        if (rec is null) return; // recording deleted before the job ran — nothing to do.

        try
        {
            var transcription = await db.Transcriptions
                .Include(t => t.Segments)
                .Include(t => t.MeetingMinutes)
                .FirstOrDefaultAsync(t => t.Id == job.TranscriptionId, ct)
                ?? throw new InvalidOperationException("Transcription not found.");

            // Protect hand-edited minutes: the automatic generator leaves them untouched. A user-initiated
            // re-create clears IsUserEdited first (so it isn't blocked here).
            if (transcription.MeetingMinutes is { IsUserEdited: true }) return;

            var names = await db.Speakers
                .Where(s => s.RecordingId == rec.Id)
                .ToDictionaryAsync(s => s.Label, s => s.DisplayName, ct);

            var segs = transcription.Segments
                .OrderBy(s => s.Ordinal)
                .Select(s => new SegmentDto(
                    s.Id,
                    s.SpeakerLabel,
                    names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                    s.StartMs, s.EndMs, s.Original, s.Revised))
                .ToList();
            if (segs.Count == 0) throw new InvalidOperationException("Transcription has no segments for minutes.");

            // Use the recording owner's effective config (their endpoint/key/model, else server defaults).
            var cfg = await resolver.ResolveAsync(rec.UserId, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var attendees = segs.Select(s => s.SpeakerDisplay).Distinct().ToList();
            var context = new MeetingMinutesContext(rec.Id, rec.CreatedAt, rec.Name ?? rec.Title, attendees, rec.DurationMs);

            // The recording's canonical actions feed the template's `action_items` field (rendered deterministically
            // by the generator), so the minutes' Action Items table always matches the Actions panel exactly.
            var actions = await db.RecordingActions
                .Where(a => a.RecordingId == rec.Id)
                .OrderBy(a => a.Ordinal)
                .Select(a => new ExtractedAction(a.Text, a.Actor, a.Deadline))
                .ToListAsync(ct);

            // The user's own note lines: they steer every prompt-driven section and feed the template's
            // `notes` field (the Enhanced notes section) when present.
            var notes = await db.MeetingNotes
                .Where(n => n.RecordingId == rec.Id)
                .OrderBy(n => n.Ordinal)
                .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt))
                .ToListAsync(ct);

            // Generate from the recording's chosen meeting type (or the General default), honouring the
            // platform-wide generation mode.
            var markdown = await generator.GenerateAsync(
                rec.UserId, rec.MeetingTypeId, context, segs, actions, notes, cfg, charBudget, ct);

            var minutes = transcription.MeetingMinutes;
            if (minutes is null)
            {
                minutes = new MinutesEntity { Id = Guid.NewGuid(), TranscriptionId = transcription.Id };
                db.MeetingMinutes.Add(minutes);
            }
            minutes.Model = cfg.Model;
            minutes.Text = markdown;
            minutes.CreatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            // Nudge the browser to refetch (status is unchanged — minutes don't own the recording status).
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());

            string? meetingTypeName = null;
            if (rec.MeetingTypeId is { } typeId)
                meetingTypeName = await db.MeetingTypes
                    .Where(m => m.Id == typeId)
                    .Select(m => m.Title)
                    .FirstOrDefaultAsync(ct);
            await PublishMinutesReadyAsync(
                webhooks, publicUrl, rec, markdown, rec.MeetingTypeId, meetingTypeName, logger, ct);
        }
        catch (Exception ex)
        {
            // Don't mark the recording Failed — the transcription (and any summary) are still valid; only the
            // minutes didn't generate. Log and leave the status untouched.
            logger.LogError(ex, "Meeting-minutes generation failed for recording {RecordingId}", rec.Id);
        }

        // The meeting type's ADDITIONAL formulas, after the minutes are saved (so one may legitimately declare
        // the Minutes context and read them). Outside the try: a primary failure doesn't cancel them, and a
        // failure here can't undo minutes that were written.
        await EnqueueAdditionalFormulasAsync(db, queue, hub, rec, logger, ct);
    }

    /// <summary>Queue one run per additional formula on the recording's meeting type, in Ordinal order. Each lands
    /// as an ordinary <see cref="FormulaResult"/> in the recording's Formulas tab.
    ///
    /// <para>This is an <b>automatic</b> run, so it replaces the recording's previous result for that formula
    /// rather than appending a duplicate every time the minutes regenerate - and it <b>skips</b> a result the user
    /// has hand-edited (see <see cref="FormulaResultUpsert"/>), exactly as the minutes themselves refuse to
    /// overwrite hand-edited minutes.</para></summary>
    private static async Task EnqueueAdditionalFormulasAsync(
        DiarizDbContext db, IJobQueue queue, IHubContext<TranscriptionHub> hub, Recording rec, ILogger logger,
        CancellationToken ct)
    {
        if (rec.MeetingTypeId is not { } typeId) return;

        var formulas = await db.MeetingTypeFormulas
            .Where(f => f.MeetingTypeId == typeId)
            .OrderBy(f => f.Ordinal)
            .Include(f => f.Formula)
            .Select(f => f.Formula!)
            .ToListAsync(ct);

        foreach (var formula in formulas)
        {
            // A Platform/Diariz formula can be disabled after a type started pointing at it (only the PRIMARY is
            // protected from that), so don't try to run one that isn't available.
            if (formula.Scope != FormulaScope.Personal && !formula.Enabled) continue;

            try
            {
                var result = await FormulaResultUpsert.ForRecordingAsync(
                    db, rec.Id, formula, rec.UserId, automatic: true, ct);
                if (result is null) continue; // the user edited this document - leave it alone.

                await db.SaveChangesAsync(ct);
                await queue.EnqueueFormulaRunAsync(
                    new FormulaRunJob(rec.Id, null, result.Id, formula.Id, rec.UserId), ct);
                await hub.NotifyFormulaStatusAsync(
                    rec.UserId, rec.Id, null, result.Id, FormulaRunStatus.Generating.ToString());
            }
            catch (Exception ex)
            {
                // One additional formula failing to queue must not stop the others, nor touch the minutes.
                logger.LogError(ex, "Could not queue formula {FormulaId} for recording {RecordingId}",
                    formula.Id, rec.Id);
            }
        }
    }

    /// <summary>Emits <c>recording.minutes_ready</c>, carrying the generated Markdown so a subscriber can act on
    /// it without a second call. Swallows its own failures - the minutes are already persisted and must not be
    /// flipped by a broken publisher (see SummarizationProcessor, which established the pattern).</summary>
    private static async Task PublishMinutesReadyAsync(
        IWebhookPublisher webhooks, string publicUrl, Recording rec, string minutesText,
        Guid? meetingTypeId, string? meetingTypeName, ILogger logger, CancellationToken ct)
    {
        try
        {
            await webhooks.PublishAsync(WebhookEventTypes.RecordingMinutesReady, rec.UserId, new
            {
                recordingId = rec.Id,
                name = rec.Name ?? rec.Title,
                status = rec.Status.ToString(),
                minutes = minutesText,
                meetingTypeId,
                meetingTypeName,
                links = WebhookPayload.For(publicUrl, rec.Id),
            }, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit recording.minutes_ready for {RecordingId}", rec.Id);
        }
    }
}
