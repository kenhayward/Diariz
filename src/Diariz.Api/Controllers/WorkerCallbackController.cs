using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Diariz.Api.Controllers;

/// <summary>
/// Internal endpoint the Python worker calls back to with results. Not user-facing;
/// authenticated by a shared secret header (X-Worker-Secret) rather than JWT.
/// </summary>
[ApiController]
[Route("internal/transcriptions")]
public class WorkerCallbackController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IJobQueue _queue;
    private readonly ILlmSettingsResolver _summarization;
    private readonly IEmbeddingSettingsResolver _embedding;
    private readonly ISpeakerIdentification _identification;
    private readonly WorkerOptions _opts;
    private readonly IWebhookPublisher _webhooks;
    private readonly IOptions<AppPublicOptions> _appOpts;
    private readonly ILogger<WorkerCallbackController> _logger;

    public WorkerCallbackController(
        DiarizDbContext db, IHubContext<TranscriptionHub> hub, IJobQueue queue,
        ILlmSettingsResolver summarization, IEmbeddingSettingsResolver embedding,
        ISpeakerIdentification identification, IOptions<WorkerOptions> opts,
        IWebhookPublisher webhooks, IOptions<AppPublicOptions> appOpts,
        ILogger<WorkerCallbackController> logger)
    {
        _db = db;
        _hub = hub;
        _queue = queue;
        _summarization = summarization;
        _embedding = embedding;
        _identification = identification;
        _opts = opts.Value;
        _webhooks = webhooks;
        _appOpts = appOpts;
        _logger = logger;
    }

    private bool SecretOk =>
        Request.Headers.TryGetValue("X-Worker-Secret", out var v) && v == _opts.CallbackSecret;

    [HttpPost("result")]
    public async Task<IActionResult> Result(TranscriptionResult body)
    {
        if (!SecretOk) return Unauthorized();

        var transcription = await _db.Transcriptions
            .Include(t => t.Recording)
            .FirstOrDefaultAsync(t => t.Id == body.TranscriptionId);
        if (transcription?.Recording is null) return NotFound();

        transcription.Language = body.Language;

        // The worker measures the real duration from the decoded audio — authoritative, and the only
        // source for uploaded files (whose client-side duration is unknown).
        if (body.DurationMs is > 0) transcription.Recording.DurationMs = body.DurationMs.Value;

        // Full-pipeline wall-clock time the worker spent on this job.
        if (body.ProcessingMs is > 0) transcription.ProcessingMs = body.ProcessingMs.Value;

        var ordinal = 0;
        foreach (var s in body.Segments)
        {
            _db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcription.Id,
                SpeakerLabel = string.IsNullOrWhiteSpace(s.Speaker) ? "UNKNOWN" : s.Speaker,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                // Collapse repeated line feeds / blank lines so the stored transcript uses a single
                // end-of-paragraph mark between lines (see TranscriptText).
                Original = TranscriptText.Normalize(s.Text),
                // Null when the worker sent none: null is what the split endpoint refuses on, and an
                // empty array would make an unsplittable segment look splittable.
                WordsJson = SegmentWords.Serialize(s.Words),
                Ordinal = ordinal++
            });
        }

        // Seed Speaker rows for any new labels (default display = label), preserving renames.
        var speakers = await _db.Speakers
            .Where(sp => sp.RecordingId == transcription.RecordingId)
            .ToListAsync();
        var byLabel = speakers.ToDictionary(sp => sp.Label);
        foreach (var label in body.Segments.Select(s => s.Speaker)
            .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct())
        {
            if (byLabel.ContainsKey(label)) continue;
            var sp = new Speaker { Id = Guid.NewGuid(), RecordingId = transcription.RecordingId, Label = label, DisplayName = label };
            _db.Speakers.Add(sp);
            byLabel[label] = sp;
        }

        // Attach the worker's per-speaker embeddings, then auto-identify against the owner's voiceprints.
        foreach (var se in body.Speakers ?? [])
        {
            if (se.Embedding is not { Length: > 0 } || !byLabel.TryGetValue(se.Speaker, out var sp)) continue;
            sp.Embedding = new Vector(se.Embedding);
        }
        // Speech comes from the payload rather than the database: these segments are not saved yet, so a query
        // here would measure the previous transcription instead of the one being written.
        await _identification.ApplyAsync(
            byLabel.Values,
            SpeakerSpeech.FromSegments(
                body.Segments.Select(sg => (sg.Speaker ?? "", sg.StartMs, sg.EndMs))));

        transcription.Recording.Error = null;  // clear any error from a prior failed attempt

        // A successful run that found no speech is a dead end, not a transcript: every downstream job
        // (summary, actions, tags, minutes, embeddings) needs segments, so queueing them only converts a
        // clear failure into a confusing one - this used to surface as the summariser's own complaint,
        // "Transcription has no segments to summarise", which describes its problem rather than the user's.
        // The usual cause is a capture that recorded nothing: most often sharing a screen or window in a
        // browser on Linux, where Chromium does not implement system-audio capture and the stream is
        // silent (tab sharing does carry audio). Fail it here, in the user's terms.
        if (body.Segments is null or { Count: 0 })
        {
            transcription.Recording.Status = RecordingStatus.Failed;
            transcription.Recording.Error = "No speech was detected in this recording.";
            await _db.SaveChangesAsync();
            await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
                RecordingStatus.Failed.ToString());
            return Ok();
        }

        // Continue the pipeline: when summarisation is configured for the owner, kick it off
        // automatically (which also auto-names the recording when it has no name yet).
        var cfg = await _summarization.ResolveAsync(LlmCallKind.Summarize);
        var autoSummarise = cfg.Enabled;
        transcription.Recording.Status = autoSummarise ? RecordingStatus.Summarizing : RecordingStatus.Transcribed;

        await _db.SaveChangesAsync();

        // Collapse consecutive same-speaker segments when the owner has asked for it. Deliberately here:
        // after SpeakerLabeling has been saved, so two diarization labels resolved to one person merge as
        // one speaker; and before every enqueue and the SignalR notify below, so the summary, actions,
        // tags, embeddings, the browser and the webhook all see one final shape rather than a reshuffle.
        // FirstOrDefaultAsync over a bool projection yields false when the owner has no settings row.
        var autoMerge = await _db.UserSettings
            .Where(x => x.UserId == transcription.Recording.UserId)
            .Select(x => x.AutoMergeSpeakerSegments)
            .FirstOrDefaultAsync();
        if (autoMerge)
        {
            try
            {
                if (await TranscriptSegmentMerge.ApplyAsync(_db, transcription.RecordingId, transcription.Id))
                    await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: an unmerged transcript is perfectly valid, but throwing here would
                // leave the recording committed as Summarizing with no summarization job enqueued - stranded
                // in "Summarising..." with nothing left to clear it, which is exactly what the enqueue guard
                // immediately below exists to prevent.
                _logger.LogError(ex, "Auto-merge failed for transcription {TranscriptionId}", transcription.Id);
            }
        }

        if (autoSummarise)
        {
            try
            {
                await _queue.EnqueueSummarizationAsync(new SummarizationJob(transcription.RecordingId, transcription.Id));
            }
            catch
            {
                // Summarizing is already committed above, and the job we just failed to queue is the only
                // thing that clears it - POST /summarize no-ops while it is set, so the recording would sit
                // in "Summarising..." until someone thought to re-transcribe it. Put it back to a status the
                // user can act on, then let the failure surface to the worker.
                //
                // The status is deliberately committed *before* the enqueue rather than after: the summariser
                // runs in this same process and would otherwise be free to finish and write Summarized before
                // the flip landed on top of it - trading a rare stuck recording for a rarer one.
                transcription.Recording.Status = RecordingStatus.Transcribed;
                await _db.SaveChangesAsync();
                throw;
            }
            // Action items are extracted next (its worker skips recordings already extracted, so a re-transcribe
            // never clobbers manual edits). It is status-neutral (no race with the summary) and, when it finishes,
            // chains the meeting-minutes job — so the minutes render the same canonical action set.
            await _queue.EnqueueActionsAsync(new ActionsJob(transcription.RecordingId, transcription.Id));
            // Tag-cloud tags are re-extracted on every (re)transcription — the tags processor replaces the
            // previous set wholesale (tags are machine-only, so there are no manual edits to preserve).
            await _queue.EnqueueTagsAsync(new TagsJob(transcription.RecordingId, transcription.Id));
        }

        // Build/refresh the RAG index for this recording's latest transcription (status-neutral, independent of
        // summarisation — RAG can be on via a dedicated embeddings endpoint even when summarisation is off). The
        // processor no-ops if the owner has no endpoint, so only enqueue when embedding is actually configured.
        var embedCfg = await _embedding.ResolveAsync();
        if (embedCfg.Enabled)
            await _queue.EnqueueEmbeddingAsync(new EmbeddingJob(transcription.RecordingId, transcription.Id));

        await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
            transcription.Recording.Status.ToString());

        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        var rec = transcription.Recording;
        if (rec.Status == RecordingStatus.Transcribed || rec.Status == RecordingStatus.Summarizing)
        {
            object Body(IReadOnlyList<object> attendees) => new
            {
                recordingId = rec.Id, name = rec.Name ?? rec.Title, status = rec.Status.ToString(),
                durationMs = body.DurationMs, links = WebhookPayload.For(publicUrl, rec.Id),
                attendees,
            };

            await _webhooks.PublishAsync(
                WebhookEventTypes.RecordingTranscribed, rec.UserId,
                Body(await AttendeePayload.ForRecordingAsync(_db, rec.Id, includeContacts: false)),
                dataWithContacts: Body(await AttendeePayload.ForRecordingAsync(_db, rec.Id, includeContacts: true)));
        }

        return Ok();
    }

    [HttpPost("failure")]
    public async Task<IActionResult> Failure(TranscriptionFailure body)
    {
        if (!SecretOk) return Unauthorized();

        var transcription = await _db.Transcriptions
            .Include(t => t.Recording)
            .FirstOrDefaultAsync(t => t.Id == body.TranscriptionId);
        if (transcription?.Recording is null) return NotFound();

        transcription.Recording.Status = RecordingStatus.Failed;
        transcription.Recording.Error = body.Error;
        await _db.SaveChangesAsync();
        await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
            RecordingStatus.Failed.ToString());

        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        var rec = transcription.Recording;
        await _webhooks.PublishAsync(WebhookEventTypes.RecordingTranscriptionFailed, rec.UserId, new
        {
            recordingId = rec.Id, name = rec.Name ?? rec.Title, status = RecordingStatus.Failed.ToString(),
            error = body.Error, links = WebhookPayload.For(publicUrl, rec.Id),
            // A failed transcription produced no speakers; the key is present for shape consistency.
            attendees = Array.Empty<object>(),
        });

        return Ok();
    }
}
