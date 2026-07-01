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
