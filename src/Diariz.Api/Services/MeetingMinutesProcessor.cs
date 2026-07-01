using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        DiarizDbContext db, IMeetingMinutesClient client, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, MeetingMinutesJob job, int charBudget, ILogger logger,
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

            var markdown = await client.GenerateAsync(cfg, segs, rec.CreatedAt, charBudget, ct);

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
        }
        catch (Exception ex)
        {
            // Don't mark the recording Failed — the transcription (and any summary) are still valid; only the
            // minutes didn't generate. Log and leave the status untouched.
            logger.LogError(ex, "Meeting-minutes generation failed for recording {RecordingId}", rec.Id);
        }
    }
}
