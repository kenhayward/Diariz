namespace Diariz.Domain.Entities;

/// <summary>
/// One uploaded slice of a recording still being captured. Chunks arrive during the meeting rather than
/// in a single POST at the end, so the audio is durable server-side while the meeting is still running;
/// at finalise they are concatenated into the recording's canonical blob and these rows (and their
/// blobs) are deleted. A recording that has finished capture therefore has no chunks.
/// <para>
/// Chunks are stored <b>contiguous and non-overlapping</b>. Overlap for transcription is a property of
/// the decode window, not of the stored audio - the worker prepends the previous chunk's tail when it
/// transcribes - because overlapping stored chunks would duplicate audio in the concatenation.
/// </para>
/// <para>
/// Only the first chunk carries the WebM/EBML header, so an individual chunk after it is <b>not</b>
/// independently decodable. Anything that decodes a chunk must byte-join it onto the chunks before it
/// (see <c>docs/Streaming_Capture_and_Live_Transcript.md</c> §5.1 findings).
/// </para>
/// </summary>
public class RecordingChunk
{
    public Guid Id { get; set; }

    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>0-based position in the capture. Unique with <see cref="RecordingId"/>, which is what
    /// makes the chunk upload idempotent: a retry of the same sequence replaces rather than duplicates.</summary>
    public int Sequence { get; set; }

    /// <summary>Object-storage key, <c>{userId}/{recordingId}/chunks/{sequence:D5}.webm</c>. Zero-padded
    /// so a plain object-store listing sorts into capture order, which is the only way to reconstruct
    /// the sequence if these rows are ever lost.</summary>
    public string BlobKey { get; set; } = string.Empty;

    /// <summary>Span covered by this chunk, in the recording's own (pause-aware) clock - the same clock
    /// <see cref="Segment.StartMs"/> is measured in.</summary>
    public long StartMs { get; set; }
    public long EndMs { get; set; }

    /// <summary>Size of the stored blob. Counts toward the owner's quota while the recording is live,
    /// and is reconciled against the concatenated blob at finalise.</summary>
    public long SizeBytes { get; set; }

    /// <summary>When the upload landed. The newest chunk's value is what the reaper measures a
    /// disappeared client against.</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this chunk's live transcription came back, or null while it is still outstanding.
    /// <para>
    /// The oldest null is how far behind the live transcript has fallen: past a threshold the API stops
    /// queueing live work rather than building an unbounded backlog. Null forever is normal and harmless
    /// for a chunk whose live pass failed or was skipped - the final full-file pass covers the meeting
    /// regardless, so this only ever gates the running commentary.
    /// </para></summary>
    public DateTimeOffset? TranscribedAt { get; set; }
}
