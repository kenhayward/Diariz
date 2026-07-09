using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Processes a single folder-summary job: ensures every included recording (across the section and
/// its child sections) has an individual summary - generating and persisting any that are missing - then
/// combines them into one folder summary persisted on the section. Static so it can be unit-tested with fake
/// clients + an in-memory DbContext, mirroring <see cref="SummarizationProcessor"/>.</summary>
public static class SectionSummaryProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, ISummarizationClient perRecording, IMeetingMinutesClient combiner,
        ISummarizationSettingsResolver resolver, IHubContext<TranscriptionHub> hub,
        string perRecordingTemplate, string folderTemplate,
        SectionSummaryJob job, int combineCharBudget, ILogger logger, CancellationToken ct = default)
    {
        var section = await db.Sections
            .Include(s => s.Summary)
            .FirstOrDefaultAsync(s => s.Id == job.SectionId, ct);
        if (section is null) return; // section deleted before the job ran.

        var summary = section.Summary;

        // Protect a hand-edited folder summary: leave it untouched (an explicit regenerate clears the flag first).
        if (summary is { IsUserEdited: true })
        {
            summary.Status = SectionGenerationStatus.Ready;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "summary", "Ready");
            return;
        }

        try
        {
            var cfg = await resolver.ResolveAsync(section.UserId, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var items = new List<(string RecordingName, string Summary)>();
            foreach (var rec in await IncludedRecordingsAsync(db, section))
            {
                var text = await EnsureRecordingSummaryAsync(db, perRecording, resolver, cfg, rec.Id, perRecordingTemplate, ct);
                if (!string.IsNullOrWhiteSpace(text)) items.Add((rec.Name ?? rec.Title, text!));
            }

            var folderText = items.Count == 0
                ? "" // empty folder / no summarisable recordings — nothing to combine.
                : await combiner.GenerateAsync(cfg, FolderSummaryPrompt.BuildMessages(folderTemplate, items, combineCharBudget), ct);

            summary = await UpsertAsync(db, section);
            summary.Model = cfg.Model;
            summary.Text = folderText;
            summary.CreatedAt = DateTimeOffset.UtcNow;
            summary.Status = SectionGenerationStatus.Ready;
            summary.Error = null;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "summary", "Ready");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Folder-summary generation failed for section {SectionId}", section.Id);
            var failed = await UpsertAsync(db, section);
            failed.Status = SectionGenerationStatus.Failed;
            failed.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "summary", "Failed");
        }
    }

    /// <summary>The recordings filed directly under the section or under any of its child sections
    /// (ownership-scoped). Explicit query (not a filtered Include) so Npgsql and the in-memory provider agree.</summary>
    internal static async Task<List<RecordingRef>> IncludedRecordingsAsync(DiarizDbContext db, Section section)
    {
        var allIds = await db.Sections
            .Where(s => s.UserId == section.UserId && s.ParentId == section.Id)
            .Select(s => s.Id).ToListAsync();
        allIds.Add(section.Id);
        // `SectionId.HasValue &&` guards the `.Value` so Ungrouped (null-section) recordings don't throw under
        // the in-memory provider; on Npgsql it translates to a plain `SectionId IN (...)`.
        return await db.Recordings
            .Where(r => r.UserId == section.UserId && r.SectionId.HasValue && allIds.Contains(r.SectionId.Value))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new RecordingRef(r.Id, r.Name, r.Title))
            .ToListAsync();
    }

    /// <summary>Returns the recording's current-transcription summary text, generating &amp; persisting it on
    /// that transcription first if missing. Recordings with no transcription/segments contribute nothing.</summary>
    private static async Task<string?> EnsureRecordingSummaryAsync(
        DiarizDbContext db, ISummarizationClient client, ISummarizationSettingsResolver resolver,
        SummarizationRequestConfig cfg, Guid recordingId, string template, CancellationToken ct)
    {
        var transcription = await db.Transcriptions
            .Include(t => t.Segments)
            .Include(t => t.Summary)
            .Where(t => t.RecordingId == recordingId)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
        if (transcription is null) return null;

        if (transcription.Summary is { } existing && !string.IsNullOrWhiteSpace(existing.Text))
            return existing.Text; // already has one (incl. hand-edited) — reuse, don't regenerate.

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

        var result = await client.SummarizeAsync(cfg, segs, needName: false, template, ct);
        var summary = transcription.Summary;
        if (summary is null)
        {
            summary = new Summary { Id = Guid.NewGuid(), TranscriptionId = transcription.Id };
            db.Summaries.Add(summary);
        }
        summary.Model = cfg.Model;
        summary.Text = result.Summary;
        summary.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); // per-recording save: partial progress survives a later failure.
        return result.Summary;
    }

    private static async Task<SectionSummary> UpsertAsync(DiarizDbContext db, Section section)
    {
        var summary = section.Summary ?? await db.SectionSummaries.FirstOrDefaultAsync(x => x.SectionId == section.Id);
        if (summary is null)
        {
            summary = new SectionSummary { Id = Guid.NewGuid(), SectionId = section.Id };
            db.SectionSummaries.Add(summary);
            section.Summary = summary;
        }
        return summary;
    }

    public record RecordingRef(Guid Id, string? Name, string Title);
}
