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
    IOptions<WorkerOptions> opts,
    IOptions<LiveCaptureOptions>? live = null,
    ISpeakerIdentification? identification = null) : ControllerBase
{
    private readonly WorkerOptions _opts = opts.Value;
    private readonly LiveCaptureOptions _live = live?.Value ?? new LiveCaptureOptions();

    /// <summary>How many ordinals each chunk owns. A chunk's segments are numbered
    /// <c>Sequence * OrdinalStride + i</c>, which is what makes the assignment cost proportional to the
    /// arriving chunk rather than to the whole meeting (issue #753).
    /// <para>The band only has to be wider than any chunk's segment count. Chunks are capped at 45 s and
    /// a measured 45 s chunk holds about ten segments, so 10,000 is four orders of magnitude of room. If
    /// one ever overflowed, its later segments would sort into the next chunk's band - ordering would
    /// degrade at that seam, and nothing would break.</para></summary>
    private const int OrdinalStride = 10_000;

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

        // At-least-once delivery means the same chunk WILL arrive twice. Segments are replaced either
        // way, but the centroid must not absorb the same vector a second time: a running mean that
        // double-counts is quietly wrong in a way no single-chunk test would catch, dragging a voice
        // toward whichever chunk happened to be retried.
        var isRedelivery = existing.Count > 0;
        db.Segments.RemoveRange(existing);

        // Chunk-local labels mean nothing across chunks - pyannote clusters each one independently - so
        // they are mapped onto labels that hold for the whole meeting before anything is stored.
        var sessionOf = await StitchAsync(rec, transcription, body, isRedelivery);

        // Ordinal is what every reader sorts by, and it is assigned from THIS CHUNK alone: sorted within
        // the chunk by recording time, then numbered into the chunk's own band. Nothing else is read and
        // nothing already written is touched, which is what keeps the per-chunk cost flat over a long
        // meeting - see OrdinalStride and issue #753.
        //
        // Correct because chunks are contiguous and non-overlapping in recording time, so sequence order
        // IS time order across chunks. That is not a coincidence to lean on lightly - it is the same
        // invariant the canonical audio depends on, since the finalise step concatenates the chunks in
        // sequence and would duplicate or drop audio if they overlapped or left gaps.
        //
        // Sorting within the chunk is the part that is not implied by sequence: a chunk's own segments
        // can arrive out of time order, and reading order has to follow the meeting rather than the list.
        var ordered = body.Segments
            .OrderBy(s => s.StartMs).ThenBy(s => s.EndMs)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var chunkLabel = string.IsNullOrWhiteSpace(s.Speaker) ? "UNKNOWN" : s.Speaker;
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcription.Id,
                SpeakerLabel = sessionOf.GetValueOrDefault(chunkLabel, chunkLabel),
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Original = TranscriptText.Normalize(s.Text),
                WordsJson = SegmentWords.Serialize(s.Words),
                ChunkSequence = body.Sequence,
                Ordinal = body.Sequence * OrdinalStride + i,
            });
        }

        // Mark the chunk done, so the lag calculation stops counting it. Without this the oldest
        // outstanding chunk never changes and the transcript pauses itself after one threshold's worth
        // of meeting, however well the transcriber is actually keeping up.
        var chunk = await db.RecordingChunks
            .FirstOrDefaultAsync(c => c.RecordingId == rec.Id && c.Sequence == body.Sequence);
        if (chunk is not null) chunk.TranscribedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        // Only now, with this chunk's segments stored: a merge relabels by speaker across the whole
        // transcription, so one decided before the write would leave the rows it could not see behind
        // under a label it had just retired.
        await ApplyMergesAsync(rec, transcription,
            new StitchThresholds(_live.StitchThreshold, _live.StitchMargin));

        // Put names to the voices, through the SAME ranking and rules the finished-recording path uses.
        // Deliberately not a parallel copy with its own numbers: identification's operating point is one
        // thing an administrator calibrates, and a second one here would drift out of step invisibly.
        //
        // It runs on every chunk rather than once, because the centroid it judges keeps improving - a
        // voice named wrongly on a noisy first chunk has to be correctable by later evidence, and
        // SpeakerLabeling withdraws an automatic name that no longer holds.
        //
        // This NEVER enrols. See LiveIdentificationNeverEnrolsTests: an automatic match writes no
        // VoiceSample and rebuilds no shared centroid, because enrolment is platform-wide and a live
        // chunk - provisional text, a short window, a centroid still forming - is the worst possible
        // input to it.
        await IdentifyAsync(rec, transcription);

        await hub.NotifyLiveTranscriptAsync(rec.UserId, rec.Id, transcription.Id, body.Sequence);
        return Ok();
    }

    /// <summary>Map this chunk's local speaker labels onto session labels, updating the running centroids.
    ///
    /// <para>The <c>Speaker</c> rows for a live recording carry the session labels, and their
    /// <c>Embedding</c> is the running centroid. Sample counts are <b>derived</b> - the number of
    /// distinct chunks whose segments carry that label - rather than stored, so there is no second copy
    /// to fall out of step with the segments themselves, and no new column for something the data
    /// already says.</para>
    ///
    /// <para>Returns the chunk-label to session-label map. Merging converged labels is deliberately a
    /// separate pass, run once this chunk's segments exist - see <see cref="ApplyMergesAsync"/>.</para>
    /// </summary>
    private async Task<Dictionary<string, string>> StitchAsync(
        Recording rec, Transcription transcription, LiveChunkResult body, bool isRedelivery)
    {
        var speakers = await db.Speakers.Where(s => s.RecordingId == rec.Id).ToListAsync();

        var sampleCounts = (await db.Segments
                .Where(s => s.TranscriptionId == transcription.Id && s.ChunkSequence != null)
                .Select(s => new { s.SpeakerLabel, s.ChunkSequence })
                .Distinct()
                .ToListAsync())
            .GroupBy(x => x.SpeakerLabel)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var known = speakers
            .Where(s => s.Embedding is not null)
            .Select(s => new SessionCentroid(s.Label, s.Embedding!.ToArray(),
                sampleCounts.GetValueOrDefault(s.Label, 1)))
            .ToList();

        var incoming = (body.Speakers ?? [])
            .Where(se => se.Embedding is { Length: > 0 })
            .Select(se => new ChunkSpeaker(
                string.IsNullOrWhiteSpace(se.Speaker) ? "UNKNOWN" : se.Speaker, se.Embedding!, 0))
            .ToList();

        var thresholds = new StitchThresholds(_live.StitchThreshold, _live.StitchMargin);
        var decisions = LiveSpeakerStitcher.Stitch(known, incoming, thresholds);

        var sessionOf = decisions.ToDictionary(d => d.ChunkLabel, d => d.SessionLabel,
            StringComparer.Ordinal);

        foreach (var d in decisions)
        {
            var observed = incoming.First(i => i.Label == d.ChunkLabel).Embedding;
            var speaker = speakers.FirstOrDefault(s => s.Label == d.SessionLabel);
            if (speaker is null)
            {
                speaker = new Speaker
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id,
                    Label = d.SessionLabel, DisplayName = d.SessionLabel,
                    Embedding = new Pgvector.Vector(observed),
                };
                db.Speakers.Add(speaker);
                speakers.Add(speaker);
                continue;
            }

            if (isRedelivery) continue;

            var current = new SessionCentroid(speaker.Label, speaker.Embedding!.ToArray(),
                sampleCounts.GetValueOrDefault(speaker.Label, 1));
            speaker.Embedding = new Pgvector.Vector(
                LiveSpeakerStitcher.UpdateCentroid(current, observed).Centroid);
        }

        await db.SaveChangesAsync();
        return sessionOf;
    }

    /// <summary>Collapse any session labels that have converged onto one voice, moving earlier segments
    /// with them.
    ///
    /// <para>The relabel is retroactive on purpose: the split was a guess made on a noisy first chunk,
    /// and leaving the earlier text under a label the server no longer believes in would mean a person
    /// appears twice in their own transcript for the rest of the meeting.</para></summary>
    private async Task ApplyMergesAsync(
        Recording rec, Transcription transcription, StitchThresholds thresholds)
    {
        var speakers = await db.Speakers.Where(s => s.RecordingId == rec.Id).ToListAsync();

        var perChunk = await db.Segments
            .Where(s => s.TranscriptionId == transcription.Id && s.ChunkSequence != null)
            .Select(s => new { s.SpeakerLabel, s.ChunkSequence })
            .Distinct()
            .ToListAsync();

        var samples = perChunk.GroupBy(x => x.SpeakerLabel)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Labels pyannote heard in the same chunk and called different people. That judgement had the
        // actual audio - two voices overlapping, answering each other - and beats any comparison of
        // centroids across windows, so it is protected rather than weighed.
        var neverTogether = new HashSet<(string, string)>();
        foreach (var chunk in perChunk.GroupBy(x => x.ChunkSequence))
        {
            var labels = chunk.Select(x => x.SpeakerLabel).Distinct().ToList();
            foreach (var a in labels)
                foreach (var b in labels)
                    if (a != b) neverTogether.Add((a, b));
        }

        // A speaker the user has named by hand is never merged away. They know who it is and the
        // centroids do not; overruling that would discard the one piece of ground truth in the meeting.
        var mergeable = speakers
            .Where(s => s.Embedding is not null && s.PersonId is null && s.DisplayName == s.Label)
            .Select(s => new SessionCentroid(s.Label, s.Embedding!.ToArray(),
                samples.GetValueOrDefault(s.Label, 1)))
            .ToList();

        var merges = LiveSpeakerStitcher.FindMerges(mergeable, thresholds, neverTogether);
        if (merges.Count == 0) return;

        foreach (var (from, into) in merges)
        {
            // One statement, not one per segment: a 90-minute meeting carries thousands, and a merge can
            // land on any chunk. ExecuteUpdate is relational-only, so the unit provider takes the loop -
            // the single-statement path is exercised for real in the integration tests.
            if (db.Database.IsRelational())
            {
                await db.Segments
                    .Where(s => s.TranscriptionId == transcription.Id && s.SpeakerLabel == from)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.SpeakerLabel, into));
            }
            else
            {
                var affected = await db.Segments
                    .Where(s => s.TranscriptionId == transcription.Id && s.SpeakerLabel == from)
                    .ToListAsync();
                foreach (var s in affected) s.SpeakerLabel = into;
            }

            var loser = speakers.FirstOrDefault(s => s.Label == from);
            if (loser is not null) db.Speakers.Remove(loser);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Rank each session voice against the platform voiceprint directory and apply the verdict.
    ///
    /// <para>Speech per label is measured from the segments now stored, which is what
    /// <c>IdentificationRules</c> needs for its minimum-speech floor: a voice heard for a second and a
    /// half is not scored at all, live or otherwise.</para></summary>
    private async Task IdentifyAsync(Recording rec, Transcription transcription)
    {
        if (identification is null) return;

        // The embedding filter is applied in memory: vector columns are mapped only under Npgsql, so a
        // `s.Embedding != null` in the query cannot be translated by the in-memory test provider.
        var speakers = (await db.Speakers.Where(s => s.RecordingId == rec.Id).ToListAsync())
            .Where(s => s.Embedding is not null)
            .ToList();
        if (speakers.Count == 0) return;

        var speechByLabel = (await db.Segments
                .Where(s => s.TranscriptionId == transcription.Id)
                .Select(s => new { s.SpeakerLabel, Ms = s.EndMs - s.StartMs })
                .ToListAsync())
            .GroupBy(x => x.SpeakerLabel)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Ms), StringComparer.Ordinal);

        await identification.ApplyAsync(speakers, speechByLabel);
        await db.SaveChangesAsync();
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
