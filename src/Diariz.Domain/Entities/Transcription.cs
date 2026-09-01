namespace Diariz.Domain.Entities;

/// <summary>
/// One transcription pass over a recording. A recording can have several versions
/// (e.g. after re-transcribing with a different model); the highest <see cref="Version"/>
/// is the current one, older versions are retained for comparison.
/// </summary>
public class Transcription
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Model identifier used, e.g. "whisperx-large-v3".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Monotonic version number per recording, starting at 1.</summary>
    public int Version { get; set; }

    /// <summary>True while this is the <b>live</b> pass over a capture still in progress: a partial
    /// transcript, assembled chunk by chunk, that the final full-file pass supersedes by writing the
    /// next version.
    /// <para>
    /// Nothing downstream may act on it. Summaries, action items, tags, meeting minutes and embeddings
    /// all decline it, exports refuse it, and search and the MCP tools exclude it - see the table in
    /// <c>docs/Streaming_Capture_and_Live_Transcript.md</c> section 7.2. Several of those are one-shot:
    /// action and tag extraction stamp a "done" marker even when they find nothing, so running one on
    /// half a meeting would make the real pass skip it and silently lose the rest.
    /// </para>
    /// <para>
    /// The detail endpoint <b>does</b> return it, flagged, because the UI has to render it while the
    /// meeting is running.
    /// </para></summary>
    public bool IsProvisional { get; set; }

    /// <summary>Detected language code (ISO-639-1) if available.</summary>
    public string? Language { get; set; }

    /// <summary>Full-pipeline wall-clock time the worker spent producing this transcription, in
    /// milliseconds (null for transcriptions created before this was tracked).</summary>
    public long? ProcessingMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Segment> Segments { get; set; } = new List<Segment>();
    public Summary? Summary { get; set; }
    public MeetingMinutes? MeetingMinutes { get; set; }
}
