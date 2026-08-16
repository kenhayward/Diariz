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
/// and replaces the recording's <see cref="RecordingTagStatus.Suggested"/> <see cref="RecordingTag"/>s -
/// adopted and dismissed rows are the user's own and are left alone. Unlike <see cref="ActionsProcessor"/>
/// there is no once-only guard — a re-transcription simply refreshes the suggestions. Instead it guards
/// against STALE jobs: a slow/backfilled job for a superseded transcription version must not overwrite the
/// newer version's tags. Status-neutral (never touches
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

        // Attribute every model call this job makes. Pushed once here rather than at the client, so the
        // handler deep inside HttpClient can record who asked and why.
        using var llm = LlmCallScope.Push(
            LlmCallKind.Tags, rec.UserId, await OwnerEmailAsync(db, rec.UserId, ct),
            rec.Id, rec.Name ?? rec.Title);

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

            // Replace only AFTER a successful extraction so a failed re-run keeps the previous set - and
            // replace only the SUGGESTIONS. Adopted tags are the user's own and must survive a
            // re-transcription; dismissals are tombstones that stop a word coming back here.
            var keep = rec.Tags
                .Where(t => t.Status != RecordingTagStatus.Suggested)
                .ToList();
            // Materialised, like `keep` above: RemoveRange enumerates what it is given while it marks each
            // entity Deleted, so handing it a live query over the same collection it is mutating is a trap
            // waiting for the day RemoveRange starts touching rec.Tags. It costs nothing here (<= 12 rows).
            var replace = rec.Tags
                .Where(t => t.Status == RecordingTagStatus.Suggested)
                .ToList();
            db.RecordingTags.RemoveRange(replace);

            // Never re-offer a word the user already holds or has already rejected on this recording. Compare
            // NORMALISED forms, not raw text: an adopted (or dismissed) tag can be stored in a different
            // spelling than whatever the LLM returns next time - Task 6's AddTag rewrites an adopted row to
            // TagText.Normalize's hyphenated form ("Data-Collection"), while extraction always hands back its
            // own raw, un-normalised text ("Data Collection"). Comparing raw strings would miss that they are
            // the same word, defeating this guard in both directions - re-suggesting a tag the user already
            // holds, and resurrecting one they dismissed.
            var spoken = keep
                .Select(t => TagText.Normalize(t.Tag))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Suggestions are stored in their NORMALISED form (TagText.Normalize, which also does the
            // 64-char truncation, so no manual slicing here). TagsPrompt.ParseResponse only dedupes the raw
            // LLM text, so "Data Collection" and "Data-Collection" both survive that step as distinct
            // candidates while normalising to the same thing - inserting both would try to write two rows
            // whose lower(Tag) happens to differ (so the unique index would not catch it) but which the
            // user experiences as one duplicated hint. Dedupe by normalised form here too, keeping the first
            // (highest-weighted, same rule ParseResponse itself uses) of any clash.
            var ordinal = 0;
            var newTags = new List<RecordingTag>();
            var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in extracted)
            {
                var normalized = TagText.Normalize(e.Tag);
                if (normalized is null) continue;               // nothing usable left - drop it.
                if (spoken.Contains(normalized)) continue;       // already held or dismissed here.
                if (!seenNormalized.Add(normalized)) continue;   // duplicate within this batch.

                newTags.Add(new RecordingTag
                {
                    Id = Guid.NewGuid(),
                    RecordingId = rec.Id,
                    Tag = normalized,
                    Weight = Math.Clamp(e.Weight, 0.0, 1.0),
                    Ordinal = ordinal++,
                    Status = RecordingTagStatus.Suggested,
                });
            }
            db.RecordingTags.AddRange(newTags);
            // Set even when zero tags came back: a thin transcript is "done", not retry-forever.
            rec.TagsExtractedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            // Nudge the browser to refetch (status is unchanged — tags don't own the recording status).
            await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
            await PublishTagsReadyAsync(
                db, webhooks, publicUrl, rec, newTags, logger, ct);
        }
        catch (Exception ex)
        {
            // Don't mark the recording Failed — the transcript (and summary/minutes) are still valid; only
            // tag extraction didn't run. Log and leave status, tags, and TagsExtractedAt untouched.
            logger.LogError(ex, "Tag extraction failed for recording {RecordingId}", rec.Id);
        }
    }

    private static Task<string?> OwnerEmailAsync(DiarizDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);

    /// <summary>Emits <c>recording.tags_ready</c>, carrying the freshly extracted tags so a subscriber can act on
    /// them without a second call. Swallows its own failures - the tags are already persisted and must not be
    /// flipped by a broken publisher (see SummarizationProcessor, which established the pattern).</summary>
    private static async Task PublishTagsReadyAsync(
        DiarizDbContext db, IWebhookPublisher webhooks, string publicUrl, Recording rec, IReadOnlyList<RecordingTag> tags,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            // The same body twice, differing only in whether the attendees carry contact details. Only
            // subscriptions that opted in receive the second one.
            object Body(IReadOnlyList<object> attendees) => new
            {
                recordingId = rec.Id,
                name = rec.Name ?? rec.Title,
                status = rec.Status.ToString(),
                tags = tags.Select(t => new { name = t.Tag, weight = t.Weight }),
                count = tags.Count,
                links = WebhookPayload.For(publicUrl, rec.Id),
                attendees,
            };

            await webhooks.PublishAsync(
                WebhookEventTypes.RecordingTagsReady, rec.UserId,
                Body(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: false, ct)),
                dataWithContacts: Body(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: true, ct)),
                ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit recording.tags_ready for {RecordingId}", rec.Id);
        }
    }
}
