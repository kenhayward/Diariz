using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>
/// A windowed span of a transcription's segments, embedded for semantic (RAG) retrieval. Supersedes the
/// unused per-segment <see cref="Segment.Embedding"/>: a single segment is too small a retrieval unit, so we
/// embed windows of consecutive segments (see <c>TranscriptChunker</c>). Chunks are replaced wholesale on each
/// (re)transcription and cascade-delete with their <see cref="Transcription"/>. <see cref="RecordingId"/> and
/// <see cref="UserId"/> are denormalized so a pgvector KNN query can owner-filter without joins.
/// </summary>
public class TranscriptChunk
{
    public Guid Id { get; set; }

    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }

    /// <summary>Denormalized owning recording (for citation deep-links + fast scoping).</summary>
    public Guid RecordingId { get; set; }

    /// <summary>Denormalized owner (for the owner-scoped vector pre-filter).</summary>
    public Guid UserId { get; set; }

    /// <summary>Ordering index of the chunk within the transcription.</summary>
    public int Ordinal { get; set; }

    /// <summary>Span of the covered segments, relative to the start of the recording.</summary>
    public long StartMs { get; set; }
    public long EndMs { get; set; }

    /// <summary>Comma-separated distinct speaker display names covered by the chunk (for citation/who-said).</summary>
    public string SpeakerLabels { get; set; } = string.Empty;

    /// <summary>The flattened "Speaker: Text" body that was embedded.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The embedding vector (dimension pinned to the server embed model - 768 for nomic-embed-text).
    /// Nullable so a row can exist before/without an embedding endpoint (it just won't be retrieved).</summary>
    public Vector? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
