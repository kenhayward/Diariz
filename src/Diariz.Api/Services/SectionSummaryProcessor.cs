using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services.Llm;
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
        ILlmSettingsResolver resolver, IHubContext<TranscriptionHub> hub,
        string perRecordingTemplate, string folderTemplate,
        SectionSummaryJob job, ILogger logger, CancellationToken ct = default)
    {
        var section = await db.Sections
            .Include(s => s.Summary)
            .FirstOrDefaultAsync(s => s.Id == job.SectionId, ct);
        if (section is null) return; // section deleted before the job ran.

        // Attribute every model call this job makes (including the per-recording summaries it regenerates
        // along the way - that fan-out is counted as turns within this one operation).
        using var llm = LlmCallScope.Push(
            LlmCallKind.SectionSummary, section.UserId, await OwnerEmailAsync(db, section.UserId, ct),
            sectionId: section.Id, sectionName: section.Name);

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
            var cfg = await resolver.ResolveAsync(LlmCallKind.SectionSummary, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var items = new List<(string RecordingName, string Summary)>();
            foreach (var rec in await IncludedRecordingsAsync(db, section))
            {
                var text = await EnsureRecordingSummaryAsync(db, perRecording, resolver, cfg, rec.Id, perRecordingTemplate, ct);
                if (!string.IsNullOrWhiteSpace(text)) items.Add((rec.Name ?? rec.Title, text!));
            }

            var folderText = items.Count == 0
                ? "" // empty folder / no summarisable recordings — nothing to combine.
                // Budget comes from the resolved config (one platform-wide number sized off the model's
                // context window), not a per-worker constant - see LlmContextBudget.
                : await combiner.GenerateAsync(
                    cfg, FolderSummaryPrompt.BuildMessages(folderTemplate, items, cfg.ContextCharBudget), ct);

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
            // Deliberately NOT ct. The commonest way to land here is the host cancelling mid-call on a
            // shutdown or redeploy, and that same token would cancel this write - leaving the folder in
            // Generating forever while the worker's finally acks the stream entry, so nothing ever retries
            // it and no error is recorded. This write is what ends the job; it has to outlive the cancel.
            await db.SaveChangesAsync(CancellationToken.None);
            await hub.NotifySectionStatusAsync(section.UserId, section.Id, "summary", "Failed");
        }
    }

    /// <summary>Settles a folder whose folder-summary job was dropped without ever running - the reclaimer
    /// abandoning a message past its delivery cap. Nothing else will move it: the job is gone, and the
    /// generate endpoint is a no-op while the status reads Generating, so without this the folder shows
    /// "Generating..." indefinitely with no error anywhere to explain it.
    ///
    /// <para>Only a folder still sitting in Generating is touched. An abandoned message can be hours old,
    /// and in that time the user may have regenerated or hand-written it; failing it then would report a
    /// dead job against work that has since succeeded.</para></summary>
    public static async Task AbandonAsync(
        DiarizDbContext db, IHubContext<TranscriptionHub> hub, SectionSummaryJob job, ILogger logger,
        CancellationToken ct = default)
    {
        var summary = await db.SectionSummaries.FirstOrDefaultAsync(x => x.SectionId == job.SectionId, ct);
        if (summary is null || summary.Status != SectionGenerationStatus.Generating) return;

        var section = await db.Sections.FirstOrDefaultAsync(x => x.Id == job.SectionId, ct);
        if (section is null) return;

        logger.LogError(
            "Folder-summary generation for section {SectionId} was abandoned by the queue; marking it failed",
            section.Id);
        summary.Status = SectionGenerationStatus.Failed;
        summary.Error = "Folder-summary generation was abandoned after repeated failures. Try again.";
        await db.SaveChangesAsync(ct);
        await hub.NotifySectionStatusAsync(section.UserId, section.Id, "summary", "Failed");
    }

    private static Task<string?> OwnerEmailAsync(DiarizDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);

    /// <summary>The recordings filed under the section or anywhere beneath it. The folder walk is scoped by
    /// <c>RoomId</c>, matching the other four roll-up sites - it used to be scoped by <c>UserId</c>, which is
    /// indistinguishable in a personal room at two levels and wrong for a shared room over a deeper tree.
    /// Explicit query (not a filtered Include) so Npgsql and the in-memory provider agree.</summary>
    internal static async Task<List<RecordingRef>> IncludedRecordingsAsync(DiarizDbContext db, Section section)
    {
        var allIds = await SectionTree.SubtreeIdsAsync(db, section.RoomId, section.Id, default);
        // Placements are scoped to the folder's OWN room, matching every other roll-up site. This used to join
        // through the section owner's personal room, which made a shared-room folder roll up nothing at all:
        // `allIds` holds section ids from the folder's room, so against personal-room placements the two sets
        // were drawn from different rooms and never intersected. For a personal-room folder the two forms
        // select the same rows, since section.RoomId IS that personal room. `SectionId.HasValue &&` guards the
        // `.Value` so ungrouped placements don't throw under the in-memory provider.
        return await (
            from p in db.RoomRecordings
            join r in db.Recordings on p.RecordingId equals r.Id
            where p.RoomId == section.RoomId
                  && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt
            select new RecordingRef(r.Id, r.Name, r.Title)).ToListAsync();
    }

    /// <summary>Returns the recording's current-transcription summary text, generating &amp; persisting it on
    /// that transcription first if missing. Recordings with no transcription/segments contribute nothing.</summary>
    private static async Task<string?> EnsureRecordingSummaryAsync(
        DiarizDbContext db, ISummarizationClient client, ILlmSettingsResolver resolver,
        LlmRequestConfig cfg, Guid recordingId, string template, CancellationToken ct)
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
