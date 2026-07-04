using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Diariz.Api.Services;

/// <summary>
/// Processes one embedding job: windows a transcription's segments into chunks (<see cref="TranscriptChunker"/>),
/// embeds them against the owner's resolved config, and <b>replaces</b> the recording's existing chunks with the
/// fresh set (so re-transcribing never leaves stale chunks and retrieval needs no version filtering). Pulled out
/// of the BackgroundService so it can be unit-tested with a fake client + in-memory DbContext. Status-neutral -
/// embedding runs in the background and never touches <see cref="Recording.Status"/>. A no-op when the owner has
/// no embedding endpoint (RAG stays off, retrieval stays lexical).
/// </summary>
public static class EmbeddingProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, IEmbeddingClient client, IEmbeddingSettingsResolver resolver,
        EmbeddingJob job, ILogger logger, CancellationToken ct = default)
    {
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Id == job.RecordingId, ct);
        if (rec is null) return; // recording deleted before the job ran.

        var cfg = await resolver.ResolveAsync(rec.UserId, ct);
        if (!cfg.Enabled) return; // no endpoint → RAG off; leave retrieval lexical.

        var transcription = await db.Transcriptions
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == job.TranscriptionId, ct);
        if (transcription is null) return;

        var names = await db.Speakers
            .Where(s => s.RecordingId == rec.Id)
            .ToDictionaryAsync(s => s.Label, s => s.DisplayName, ct);

        var segs = transcription.Segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new ChunkSegment(
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.EffectiveText))
            .ToList();

        var drafts = TranscriptChunker.Chunk(segs);
        if (drafts.Count == 0)
        {
            // No segments (defensive): still clear any stale chunks so retrieval doesn't surface orphans.
            await ReplaceChunksAsync(db, rec.Id, [], ct);
            return;
        }

        // Prefix each chunk with the model's document task instruction (nomic: "search_document: "); empty for
        // models that don't use prefixes. The stored chunk text stays unprefixed - only the embedding input carries it.
        var inputs = drafts.Select(d => cfg.DocumentPrefix + d.Text).ToList();
        var vectors = await client.EmbedAsync(cfg, inputs, ct);

        var now = DateTimeOffset.UtcNow;
        var fresh = new List<TranscriptChunk>(drafts.Count);
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var vec = i < vectors.Count ? vectors[i] : null;
            fresh.Add(new TranscriptChunk
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcription.Id,
                RecordingId = rec.Id,
                UserId = rec.UserId,
                Ordinal = d.Ordinal,
                StartMs = d.StartMs,
                EndMs = d.EndMs,
                SpeakerLabels = string.Join(", ", d.Speakers),
                Text = d.Text,
                Embedding = vec is { Length: > 0 } ? new Vector(vec) : null,
                CreatedAt = now,
            });
        }

        await ReplaceChunksAsync(db, rec.Id, fresh, ct);
        logger.LogInformation("Embedded {Count} chunk(s) for recording {RecordingId}", fresh.Count, rec.Id);
    }

    /// <summary>Deletes the recording's existing chunks and inserts the fresh set in one save.</summary>
    private static async Task ReplaceChunksAsync(
        DiarizDbContext db, Guid recordingId, IReadOnlyList<TranscriptChunk> fresh, CancellationToken ct)
    {
        var existing = await db.TranscriptChunks.Where(c => c.RecordingId == recordingId).ToListAsync(ct);
        if (existing.Count > 0) db.TranscriptChunks.RemoveRange(existing);
        if (fresh.Count > 0) db.TranscriptChunks.AddRange(fresh);
        await db.SaveChangesAsync(ct);
    }
}
