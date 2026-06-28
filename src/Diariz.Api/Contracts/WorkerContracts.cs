namespace Diariz.Api.Contracts;

/// <summary>Job payload enqueued onto the Redis stream and consumed by the Python worker.
/// <paramref name="MinSpeakers"/>/<paramref name="MaxSpeakers"/> are optional pyannote hints (null = auto).</summary>
public record TranscriptionJob(
    Guid RecordingId,
    Guid TranscriptionId,
    string BlobKey,
    string Model,
    int? MinSpeakers = null,
    int? MaxSpeakers = null);

/// <summary>Job payload for async summarisation, consumed by the API's SummarizationWorker.</summary>
public record SummarizationJob(
    Guid RecordingId,
    Guid TranscriptionId);

/// <summary>One diarized, timestamped segment returned by the worker.</summary>
public record SegmentResult(
    string Speaker,
    long StartMs,
    long EndMs,
    string Text);

/// <summary>One diarized speaker's voice embedding (ECAPA, 192-d) for identification.</summary>
public record SpeakerEmbeddingResult(
    string Speaker,
    float[] Embedding);

/// <summary>Callback body the worker POSTs back to the API when a job completes.</summary>
public record TranscriptionResult(
    Guid TranscriptionId,
    string? Language,
    IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<SpeakerEmbeddingResult>? Speakers = null,
    long? DurationMs = null);

/// <summary>Callback body the worker POSTs when a job fails.</summary>
public record TranscriptionFailure(
    Guid TranscriptionId,
    string Error);
