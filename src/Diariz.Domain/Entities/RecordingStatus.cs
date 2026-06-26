namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of a recording as it moves through the transcription pipeline.</summary>
public enum RecordingStatus
{
    Uploaded = 0,
    Queued = 1,
    Transcribing = 2,
    Transcribed = 3,
    Summarized = 4,
    Failed = 5,
    // Appended (never renumber — values are persisted as ints).
    Summarizing = 6
}
