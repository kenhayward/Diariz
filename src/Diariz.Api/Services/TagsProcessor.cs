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
/// Processes a single tag-extraction job as part of the pipeline: builds the transcript, calls the LLM,
/// and REPLACES the recording's <see cref="RecordingTag"/>s wholesale. Tags are machine-only (never
/// user-edited), so unlike <see cref="ActionsProcessor"/> there is no once-only guard — a re-transcription
/// simply refreshes them. Instead it guards against STALE jobs: a slow/backfilled job for a superseded
/// transcription version must not overwrite the newer version's tags. Status-neutral (never touches
/// <c>Recording.Status</c>, never marks Failed); on success it notifies the owner so the browser refetches.
/// Pulled out of the BackgroundService so it can be unit-tested with a fake client + in-memory DbContext.
/// </summary>
public static class TagsProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, ITagsClient client, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, TagsJob job, string template, ILogger logger,
        IWebhookPublisher webhooks, string publicUrl,
        CancellationToken ct = default)
    {
        var rec = await db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Id == job.RecordingId, ct);
        if (rec is null) return; // recording deleted before the job ran — nothing to do.

        try
        {
            // Stale-job guard: only the latest transcription's job may (re)write the tags. A backfill job or
            // a slow queue entry referencing an older version would otherwise clobber fresher tags.
            var latestId = await db.Transcriptions
                .Where(t => t.RecordingId == rec.Id)
                .OrderByDescending(t => t.Version)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (latestId != job.TranscriptionId) return;

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
            if (segs.Count == 0) return; // nothing to tag (marker left null so a re-run can retry).

            // Use the recording owner's effective config (their endpoint/key/model, else server defaults).
            var cfg = await resolver.ResolveAsync(rec.UserId, ct);
            if (!cfg.Enabled) return; // no LLM configured — marker stays null so the backfill retries later.

            var extracted = await client.ExtractAsync(cfg, segs, template, ct);

            // Replace only AFTER a successful extraction so a failed re-run keeps the previous set.
            db.RecordingTags.RemoveRange(rec.Tags);
            var ordinal = 0;
            var newTags = extracted.Select(e => new RecordingTag
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Tag = e.Tag.Length > 64 ? e.Tag[..64] : e.Tag,
                Weight = Math.Clamp(e.Weight, 0.0, 1.0),
                Ordinal = ordinal++,
            }).ToList();
            db.RecordingTags.AddRange(newTags);
            // Set even when zero tags came back: a thin transcript is "done", not retry-forever.
            rec.TagsExtractedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            // Nudge the browser to refetch (status is unchanged — tags don't own the recording status).
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
            await PublishTagsReadyAsync(webhooks, publicUrl, rec, newTags, logger, ct);
        }
        catch (Exception ex)
        {
            // Don't mark the recording Failed — the transcript (and summary/minutes) are still valid; only
            // tag extraction didn't run. Log and leave status, tags, and TagsExtractedAt untouched.
            logger.LogError(ex, "Tag extraction failed for recording {RecordingId}", rec.Id);
        }
    }

    /// <summary>Emits <c>recording.tags_ready</c>, carrying the freshly extracted tags so a subscriber can act on
    /// them without a second call. Swallows its own failures - the tags are already persisted and must not be
    /// flipped by a broken publisher (see SummarizationProcessor, which established the pattern).</summary>
    private static async Task PublishTagsReadyAsync(
        IWebhookPublisher webhooks, string publicUrl, Recording rec, IReadOnlyList<RecordingTag> tags,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await webhooks.PublishAsync(WebhookEventTypes.RecordingTagsReady, rec.UserId, new
            {
                recordingId = rec.Id,
                name = rec.Name ?? rec.Title,
                status = rec.Status.ToString(),
                tags = tags.Select(t => new { name = t.Tag, weight = t.Weight }),
                count = tags.Count,
                links = WebhookPayload.For(publicUrl, rec.Id),
            }, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit recording.tags_ready for {RecordingId}", rec.Id);
        }
    }
}
