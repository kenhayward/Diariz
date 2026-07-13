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

/// <summary>Job payload for async meeting-minutes generation, consumed by the API's MeetingMinutesWorker.</summary>
public record MeetingMinutesJob(
    Guid RecordingId,
    Guid TranscriptionId);

/// <summary>Job payload for async action-item extraction (as part of the pipeline), consumed by the API's
/// ActionsWorker. Only auto-extracts when the recording has never had actions extracted.</summary>
public record ActionsJob(
    Guid RecordingId,
    Guid TranscriptionId);

/// <summary>Job payload for async folder-summary generation, consumed by the API's SectionSummaryWorker. The
/// owner and the included recordings are derived from the section inside the processor.</summary>
public record SectionSummaryJob(Guid SectionId);

/// <summary>Job payload for async folder-minutes generation, consumed by the API's SectionMinutesWorker. The
/// chosen meeting type is read from the persisted SectionMinutes row (set before enqueue).</summary>
public record SectionMinutesJob(Guid SectionId);

/// <summary>Job payload for an async formula run, consumed by the API's FormulaRunWorker. Exactly one of
/// <paramref name="RecordingId"/>/<paramref name="SectionId"/> is set (the other null) to scope the run;
/// <paramref name="ResultId"/> is the pre-created result row the worker flips as it progresses;
/// <paramref name="UserId"/> resolves the owner's per-user LLM config.</summary>
public record FormulaRunJob(Guid? RecordingId, Guid? SectionId, Guid ResultId, Guid FormulaId, Guid UserId);

/// <summary>Job payload for async transcript-chunk embedding (RAG index), consumed by the API's
/// EmbeddingWorker. The worker windows the transcription's segments, embeds them, and replaces the
/// recording's existing chunks. No-ops when the owner has no embedding endpoint.</summary>
public record EmbeddingJob(
    Guid RecordingId,
    Guid TranscriptionId);

/// <summary>Job payload for async tag-cloud extraction, consumed by the API's TagsWorker. Replaces the
/// recording's tags wholesale (tags are machine-only), so a re-transcription refreshes them; no-ops when
/// the owner has no LLM configured or the transcription is no longer the recording's latest version.</summary>
public record TagsJob(
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
    long? DurationMs = null,
    /// <summary>Full-pipeline wall-clock time the worker spent on this job (download + transcribe +
    /// diarize + embed), in milliseconds.</summary>
    long? ProcessingMs = null);

/// <summary>Callback body the worker POSTs when a job fails.</summary>
public record TranscriptionFailure(
    Guid TranscriptionId,
    string Error);

/// <summary>Job payload for an audio-concatenation merge, consumed by the Python worker. The worker
/// downloads <paramref name="BlobKeys"/> in order, concatenates them with ffmpeg, uploads the result to
/// <paramref name="OutputKey"/>, and reports back. <paramref name="DeleteRecordingIds"/> are echoed back so
/// the callback can remove the now-merged source recordings.</summary>
public record AudioMergeJob(
    Guid RecordingId,
    IReadOnlyList<string> BlobKeys,
    string OutputKey,
    IReadOnlyList<Guid> DeleteRecordingIds);

/// <summary>Callback body the worker POSTs when the concatenated audio is ready.</summary>
public record AudioMergeResult(
    Guid RecordingId,
    string BlobKey,
    string ContentType,
    long SizeBytes,
    long DurationMs,
    IReadOnlyList<Guid> DeleteRecordingIds);

/// <summary>Callback body the worker POSTs when the merge fails (originals are kept).</summary>
public record AudioMergeFailure(
    Guid RecordingId,
    string Error);
