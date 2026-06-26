using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>
/// Processes a single summarisation job: builds the transcript, calls the LLM, persists the
/// <see cref="Summary"/> (and an auto-name when the recording has none), updates status, and
/// notifies the owning user. Pulled out of the BackgroundService so it can be unit-tested with
/// a fake client + in-memory DbContext.
/// </summary>
public static class SummarizationProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, ISummarizationClient client, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, SummarizationJob job, ILogger logger, CancellationToken ct = default)
    {
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == job.RecordingId, ct);
        if (rec is null) return; // recording deleted before the job ran — nothing to do.

        try
        {
            var transcription = await db.Transcriptions
                .Include(t => t.Segments)
                .Include(t => t.Summary)
                .FirstOrDefaultAsync(t => t.Id == job.TranscriptionId, ct)
                ?? throw new InvalidOperationException("Transcription not found.");

            var names = await db.Speakers
                .Where(s => s.RecordingId == rec.Id)
                .ToDictionaryAsync(s => s.Label, s => s.DisplayName, ct);

            var segs = transcription.Segments
                .OrderBy(s => s.Ordinal)
                .Select(s => new SegmentDto(
                    s.Id,
                    s.SpeakerLabel,
                    names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                    s.StartMs, s.EndMs, s.Text))
                .ToList();
            if (segs.Count == 0) throw new InvalidOperationException("Transcription has no segments to summarise.");

            // Use the recording owner's effective config (their endpoint/key/model, else server defaults).
            var cfg = await resolver.ResolveAsync(rec.UserId, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var needName = string.IsNullOrWhiteSpace(rec.Name);
            var result = await client.SummarizeAsync(cfg, segs, needName, ct);

            var summary = transcription.Summary;
            if (summary is null)
            {
                summary = new Summary { Id = Guid.NewGuid(), TranscriptionId = transcription.Id };
                db.Summaries.Add(summary);
            }
            summary.Model = cfg.Model;
            summary.Text = result.Summary;
            summary.CreatedAt = DateTimeOffset.UtcNow;

            if (needName && !string.IsNullOrWhiteSpace(result.Name))
                rec.Name = result.Name!.Trim();

            rec.Status = RecordingStatus.Summarized;
            rec.Error = null;
            await db.SaveChangesAsync(ct);
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, RecordingStatus.Summarized.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Summarisation failed for recording {RecordingId}", rec.Id);
            rec.Status = RecordingStatus.Failed;
            rec.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, RecordingStatus.Failed.ToString());
        }
    }
}
