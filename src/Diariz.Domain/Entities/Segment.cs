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

    /// <summary>Ordering index within the transcription.</summary>
    public int Ordinal { get; set; }

    /// <summary>Embedding for RAG retrieval (populated in Milestone 3). Nullable until computed.</summary>
    public Vector? Embedding { get; set; }
}
