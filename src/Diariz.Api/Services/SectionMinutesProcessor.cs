using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MinutesEntity = Diariz.Domain.Entities.MeetingMinutes;

namespace Diariz.Api.Services;

/// <summary>Processes a single folder-minutes job: ensures every included recording (across the section and
/// its child sections) has individual minutes - generating any that are missing via the normal per-recording
/// generator - then reshapes them into one consolidated document following the folder's chosen meeting-type
/// template, persisted on the section. Mirrors <see cref="SectionSummaryProcessor"/>.</summary>
public static class SectionMinutesProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, IMeetingTypeMinutesGenerator generator, IMeetingMinutesClient combiner,
        ISummarizationSettingsResolver resolver, IHubContext<TranscriptionHub> hub, string folderTemplate,
        SectionMinutesJob job, int perRecordingCharBudget, int combineCharBudget, ILogger logger,
        CancellationToken ct = default)
    {
        var section = await db.Sections
            .Include(s => s.Minutes)
            .FirstOrDefaultAsync(s => s.Id == job.SectionId, ct);
        if (section is null) return;

        var minutes = section.Minutes;
        if (minutes is { IsUserEdited: true })
        {
            minutes.Status = SectionGenerationStatus.Ready;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "minutes", "Ready");
            return;
        }

        try
        {
            var cfg = await resolver.ResolveAsync(section.UserId, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var items = new List<(string RecordingName, string Minutes)>();
            foreach (var rec in await SectionSummaryProcessor.IncludedRecordingsAsync(db, section))
            {
                var text = await EnsureRecordingMinutesAsync(db, generator, cfg, rec.Id, perRecordingCharBudget, ct);
                if (!string.IsNullOrWhiteSpace(text)) items.Add((rec.Name ?? rec.Title, text!));
            }

            var type = section.Minutes?.MeetingTypeId is { } typeId
                ? await db.MeetingTypes.FirstOrDefaultAsync(t => t.Id == typeId, ct)
                : null;

            var folderText = items.Count == 0
                ? ""
                : await combiner.GenerateAsync(cfg, FolderMinutesPrompt.BuildMessages(folderTemplate, type, items, combineCharBudget), ct);

            minutes = await UpsertAsync(db, section);
            minutes.Model = cfg.Model;
            minutes.Text = folderText;
            minutes.CreatedAt = DateTimeOffset.UtcNow;
            minutes.Status = SectionGenerationStatus.Ready;
            minutes.Error = null;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "minutes", "Ready");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Folder-minutes generation failed for section {SectionId}", section.Id);
            var failed = await UpsertAsync(db, section);
            failed.Status = SectionGenerationStatus.Failed;
            failed.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "minutes", "Failed");
        }
    }

    /// <summary>Returns the recording's current-transcription minutes text, generating &amp; persisting it via
    /// the normal per-recording generator first if missing. Mirrors <see cref="MeetingMinutesProcessor"/>.</summary>
    private static async Task<string?> EnsureRecordingMinutesAsync(
        DiarizDbContext db, IMeetingTypeMinutesGenerator generator, SummarizationRequestConfig cfg,
        Guid recordingId, int charBudget, CancellationToken ct)
    {
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == recordingId, ct);
        if (rec is null) return null;

        var transcription = await db.Transcriptions
            .Include(t => t.Segments)
            .Include(t => t.MeetingMinutes)
            .Where(t => t.RecordingId == recordingId)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
        if (transcription is null) return null;

        if (transcription.MeetingMinutes is { } existing && !string.IsNullOrWhiteSpace(existing.Text))
            return existing.Text;

        var names = await db.Speakers
            .Where(s => s.RecordingId == recordingId)
            .ToDictionaryAsync(s => s.Label, s => s.DisplayName, ct);
        var segs = transcription.Segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new SegmentDto(
                s.Id, s.SpeakerLabel, names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Original, s.Revised))
            .ToList();
        if (segs.Count == 0) return null;

        var attendees = segs.Select(s => s.SpeakerDisplay).Distinct().ToList();
        var context = new MeetingMinutesContext(rec.Id, rec.CreatedAt, rec.Name ?? rec.Title, attendees, rec.DurationMs);
        var actions = await db.RecordingActions
            .Where(a => a.RecordingId == rec.Id).OrderBy(a => a.Ordinal)
            .Select(a => new ExtractedAction(a.Text, a.Actor, a.Deadline)).ToListAsync(ct);
        var notes = await db.MeetingNotes
            .Where(n => n.RecordingId == rec.Id).OrderBy(n => n.Ordinal)
            .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt)).ToListAsync(ct);

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
        return markdown;
    }

    private static async Task<SectionMinutes> UpsertAsync(DiarizDbContext db, Section section)
    {
        var minutes = section.Minutes ?? await db.SectionMinutes.FirstOrDefaultAsync(x => x.SectionId == section.Id);
        if (minutes is null)
        {
            minutes = new SectionMinutes { Id = Guid.NewGuid(), SectionId = section.Id };
            db.SectionMinutes.Add(minutes);
            section.Minutes = minutes;
        }
        return minutes;
    }
}
