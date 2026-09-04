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

    /// <summary>When the live pass finished with this chunk, one way or another, or null while it is
    /// still waiting on the transcriber.
    /// <para>
    /// Set in all three terminal cases: the chunk was <b>transcribed</b>, the worker <b>failed</b> on it,
    /// or the gate <b>never queued</b> it because the transcript was already too far behind. What it
    /// records is "no longer waiting", not "succeeded" - which is what the gate below actually needs.
    /// </para>
    /// <para>
    /// The oldest null is how far behind the live transcript has fallen: past a threshold the API stops
    /// queueing live work rather than building an unbounded backlog. It was previously
    /// <c>TranscribedAt</c> and was written only on success, so a chunk that failed or was skipped stayed
    /// null forever and pinned that measurement permanently - one failed chunk paused the live transcript
    /// for the rest of the meeting, and every refused chunk was another one (issue #758). A chunk the
    /// live pass will never come back to is settled, not outstanding; the final full-file pass covers the
    /// meeting regardless, so this only ever gates the running commentary.
    /// </para></summary>
    public DateTimeOffset? SettledAt { get; set; }
}
