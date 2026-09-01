using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>
/// Internal endpoint the Python worker calls back to with one transcribed live chunk. Not user-facing;
/// authenticated by the shared <c>X-Worker-Secret</c> header rather than JWT, like the other worker
/// callbacks.
/// <para>
/// Everything it writes lands on a <b>provisional</b> transcription, which the final full-file pass
/// supersedes by writing the next version. Nothing downstream acts on it - see
/// <c>ProvisionalTranscriptionGateTests</c>.
/// </para>
/// </summary>
[ApiController]
[Route("internal/transcriptions")]
public class LiveChunkCallbackController(
    DiarizDbContext db,
    IHubContext<TranscriptionHub> hub,
    IOptions<WorkerOptions> opts) : ControllerBase
{
    private readonly WorkerOptions _opts = opts.Value;

    private bool SecretOk =>
        Request.Headers.TryGetValue("X-Worker-Secret", out var v) && v == _opts.CallbackSecret;

    [HttpPost("live-chunk")]
    public async Task<IActionResult> LiveChunk(LiveChunkResult body)
    {
        if (!SecretOk) return Unauthorized();

        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == body.RecordingId);
        if (rec is null) return Ok();   // deleted mid-meeting; nothing to attach to.

        // Finalise can land while a chunk is still in flight. Reviving the provisional transcription
        // then would leave it as the highest version, hiding the real transcript behind partial text.
        if (rec.Status != RecordingStatus.Live) return Ok();

        var transcription = await db.Transcriptions
            .FirstOrDefaultAsync(t => t.RecordingId == rec.Id && t.IsProvisional);

        if (transcription is null)
        {
            transcription = new Transcription
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Model = "whisperx-live",
                // Version 1: the final pass writes 2 and supersedes this, because the detail endpoint
                // returns only the highest version.
                Version = 1,
                IsProvisional = true,
                Language = body.Language,
            };
            db.Transcriptions.Add(transcription);
            await db.SaveChangesAsync();
        }

        // At-least-once delivery: the same chunk WILL arrive twice. Replacing this sequence's segments
        // rather than appending is what stops a redelivery repeating a sentence in the middle of the
        // transcript - which would read as a transcription fault rather than a queue one.
        var existing = await db.Segments
            .Where(s => s.TranscriptionId == transcription.Id && s.ChunkSequence == body.Sequence)
            .ToListAsync();
        db.Segments.RemoveRange(existing);

        foreach (var s in body.Segments)
        {
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcription.Id,
                // Phase 2 stores the diarization label but shows nothing: it is only meaningful within
                // one chunk, so it is kept for the stitcher rather than displayed.
                SpeakerLabel = string.IsNullOrWhiteSpace(s.Speaker) ? "UNKNOWN" : s.Speaker,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Original = TranscriptText.Normalize(s.Text),
                WordsJson = SegmentWords.Serialize(s.Words),
                ChunkSequence = body.Sequence,
                Ordinal = 0,   // assigned below, across the whole transcription
            });
        }

        // Keep the per-speaker vectors. Nothing is named from them in this phase; they are what a later
        // pass stitches into one identity per voice across the whole meeting.
        foreach (var se in body.Speakers ?? [])
        {
            if (se.Embedding is not { Length: > 0 }) continue;
            var label = string.IsNullOrWhiteSpace(se.Speaker) ? "UNKNOWN" : se.Speaker;
            var speaker = await db.Speakers
                .FirstOrDefaultAsync(sp => sp.RecordingId == rec.Id && sp.Label == label);
            if (speaker is null)
            {
                speaker = new Speaker
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id, Label = label, DisplayName = label,
                };
                db.Speakers.Add(speaker);
            }
            speaker.Embedding = new Pgvector.Vector(se.Embedding);
        }

        // Mark the chunk done, so the lag calculation stops counting it. Without this the oldest
        // outstanding chunk never changes and the transcript pauses itself after one threshold's worth
        // of meeting, however well the transcriber is actually keeping up.
        var chunk = await db.RecordingChunks
            .FirstOrDefaultAsync(c => c.RecordingId == rec.Id && c.Sequence == body.Sequence);
        if (chunk is not null) chunk.TranscribedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        // Ordinal is what every reader sorts by, and chunks can complete out of order under retry - so
        // it is assigned across the whole transcription by recording time, not by arrival.
        var all = await db.Segments
            .Where(s => s.TranscriptionId == transcription.Id)
            .OrderBy(s => s.StartMs).ThenBy(s => s.EndMs)
            .ToListAsync();
        for (var i = 0; i < all.Count; i++) all[i].Ordinal = i;
        await db.SaveChangesAsync();

        await hub.NotifyLiveTranscriptAsync(rec.UserId, rec.Id, transcription.Id, body.Sequence);
        return Ok();
    }

    [HttpPost("live-chunk-failure")]
    public async Task<IActionResult> LiveChunkFailure(LiveChunkFailure body)
    {
        if (!SecretOk) return Unauthorized();

        // A failed chunk costs a gap in the live transcript and nothing else. Capture continues, the
        // audio is already durable, and the final pass transcribes the whole meeting regardless - so
        // this is logged, not escalated to the recording.
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == body.RecordingId);
        if (rec is not null)
            await hub.NotifyLiveTranscriptDegradedAsync(rec.UserId, rec.Id, body.Sequence);
        return Ok();
    }
}
