using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Webhooks;
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
        IHubContext<TranscriptionHub> hub, SummarizationJob job, string template, ILogger logger,
        IWebhookPublisher webhooks, string publicUrl,
        CancellationToken ct = default)
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

            // Protect a hand-edited summary: the automatic summariser leaves it untouched. A user-initiated
            // re-summarise clears IsUserEdited first (so it isn't blocked here).
            if (transcription.Summary is { IsUserEdited: true })
            {
                rec.Status = RecordingStatus.Summarized;
                rec.Error = null;
                await db.SaveChangesAsync(ct);
                await hub.NotifyStatusAsync(rec.UserId, rec.Id, RecordingStatus.Summarized.ToString());
                await PublishSummarizedAsync(webhooks, publicUrl, rec, transcription.Summary.Text, logger, ct);
                return;
            }

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
            if (segs.Count == 0) throw new InvalidOperationException("Transcription has no segments to summarise.");

            // Use the recording owner's effective config (their endpoint/key/model, else server defaults).
            var cfg = await resolver.ResolveAsync(rec.UserId, ct);
            if (!cfg.Enabled) throw new InvalidOperationException("Summarisation is not configured.");

            var needName = string.IsNullOrWhiteSpace(rec.Name);
            var result = await client.SummarizeAsync(cfg, segs, needName, template, ct);

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
            await PublishSummarizedAsync(webhooks, publicUrl, rec, summary.Text, logger, ct);
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

    /// <summary>Emits <c>recording.summarized</c>, carrying the summary text so a subscriber can act on it
    /// without a second call. Both success paths emit: a preserved hand-edited summary still means the
    /// recording reached Summarized, and a subscriber waiting on "summary ready" must not hang.
    /// Swallows its own failures - the summary is already persisted and must not be flipped to Failed by a
    /// broken publisher (see FormulaRunProcessor, which learned the same lesson).</summary>
    private static async Task PublishSummarizedAsync(
        IWebhookPublisher webhooks, string publicUrl, Recording rec, string summaryText,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await webhooks.PublishAsync(WebhookEventTypes.RecordingSummarized, rec.UserId, new
            {
                recordingId = rec.Id,
                name = rec.Name ?? rec.Title,
                status = rec.Status.ToString(),
                summary = summaryText,
                links = WebhookPayload.For(publicUrl, rec.Id),
            }, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit recording.summarized for {RecordingId}", rec.Id);
        }
    }
}
