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
    Summarizing = 6,
    /// <summary>Audio is being concatenated for a transcript merge (worker job in flight).</summary>
    Merging = 7,
    /// <summary>Capture is in progress: the recording exists and is receiving chunks, but has no
    /// canonical blob and no transcript yet. Set by <c>POST /api/recordings/live</c> and left behind
    /// only by a client that disappeared, which the reaper collects.</summary>
    Live = 8
}
