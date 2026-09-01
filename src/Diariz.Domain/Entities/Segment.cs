using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>
/// A contiguous span of transcribed speech attributed to a single speaker,
/// with timestamps relative to the start of the recording.
/// </summary>
public class Segment
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }

    /// <summary>Diarization label from the pipeline, e.g. "SPEAKER_00" or "UNKNOWN".</summary>
    public string SpeakerLabel { get; set; } = "UNKNOWN";

    public long StartMs { get; set; }
    public long EndMs { get; set; }

    /// <summary>The model's verbatim output for this span — immutable once written by the worker.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>A user edit or a translation of <see cref="Original"/>. Null = unchanged (show the original).
    /// A non-null value (including empty) takes precedence — see <see cref="EffectiveText"/>.</summary>
    public string? Revised { get; set; }

    /// <summary>The text actually shown/exported: the revision when present, else the model's original.</summary>
    public string EffectiveText => Revised ?? Original;

    /// <summary>Which live chunk produced this segment, or null for a segment from an ordinary
    /// full-recording pass.
    /// <para>
    /// It exists so a <b>redelivered</b> chunk can replace exactly its own segments. Redis streams are
    /// at-least-once, so the same chunk will arrive twice in production; without this the handler could
    /// only append, and the transcript would repeat a sentence somewhere in the middle - which reads as
    /// a transcription fault rather than a queue one.
    /// </para></summary>
    public int? ChunkSequence { get; set; }

    /// <summary>Ordering index within the transcription.</summary>
    public int Ordinal { get; set; }

    /// <summary>Aligned word timings as JSON (<c>[{"w":"Hi","s":1,"e":2}]</c>), or null when there are
    /// none - every recording transcribed before these were kept, and any language with no alignment
    /// model. Read and written through <c>SegmentWords</c>; a segment with null here cannot be split,
    /// because a cut point estimated from the text would slice the wrong audio.</summary>
    public string? WordsJson { get; set; }

    /// <summary>Embedding for RAG retrieval (populated in Milestone 3). Nullable until computed.</summary>
    public Vector? Embedding { get; set; }
}
