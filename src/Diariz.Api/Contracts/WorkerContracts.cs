namespace Diariz.Api.Contracts;

/// <summary>Job payload enqueued onto the Redis stream and consumed by the Python worker.
/// <paramref name="MinSpeakers"/>/<paramref name="MaxSpeakers"/> are optional pyannote hints (null = auto).
/// <paramref name="Language"/> pins the spoken language as a <b>Whisper</b> code ("en", "pt" - not the
/// platform's BCP-47 tag, which Whisper would not recognise); null lets Whisper detect it.</summary>
public record TranscriptionJob(
    Guid RecordingId,
    Guid TranscriptionId,
    string BlobKey,
    string Model,
    int? MinSpeakers = null,
    int? MaxSpeakers = null,
    string? Language = null);

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

/// <summary>A span of a recording's audio, in ms from its start, that trains a voiceprint. Stored on the
/// voice sample and sent to the worker verbatim.
///
/// <para>Spans rather than segment ids, because segment rows belong to a transcription <em>version</em>:
/// a re-transcribe replaces every one of them, and stored ids would dangle where wall-clock times do
/// not.</para></summary>
public record VoiceprintSpan(long StartMs, long EndMs);

/// <summary>One aligned word inside a segment. The single-letter members are deliberate: this is stored as
/// jsonb on every segment and a long meeting carries roughly 10k of them, so the key names are part of the
/// storage cost. <c>S</c> and <c>E</c> are ms from the start of the recording, matching
/// <see cref="SegmentResult"/>.</summary>
public record SegmentWord(string W, long S, long E);

/// <summary>One diarized, timestamped segment returned by the worker.</summary>
public record SegmentResult(
    string Speaker,
    long StartMs,
    long EndMs,
    string Text,
    /// <summary>Aligned word timings, or null when whisperx produced none - a language with no alignment
    /// model, or a recording transcribed before these were kept. A segment without them cannot be
    /// split.</summary>
    IReadOnlyList<SegmentWord>? Words = null);

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

/// <summary>Job payload for an on-demand voiceprint re-embed, consumed by the Python worker. It downloads
/// <paramref name="BlobKey"/>, slices exactly <paramref name="Spans"/> out of the waveform, embeds them with
/// ECAPA and reports back.
///
/// <para>No Whisper and no pyannote involved, so it is seconds of work - but it shares the worker process
/// with transcription, so it can queue behind one. The UI shows it as pending rather than pretending
/// otherwise.</para>
///
/// <para>An empty <paramref name="Spans"/> means the whole speaker, matching the column's null.</para></summary>
public record VoiceprintJob(
    Guid VoiceSampleId,
    Guid RecordingId,
    string BlobKey,
    IReadOnlyList<VoiceprintSpan> Spans);

/// <summary>Callback body the worker POSTs when a re-embed succeeds. <paramref name="UsedMs"/> may be less
/// than <paramref name="SelectedMs"/>, because the worker still caps how much audio it pools - the UI states
/// both rather than implying the whole selection was used.</summary>
public record VoiceprintResult(Guid VoiceSampleId, float[] Embedding, int UsedMs, int SelectedMs);

/// <summary>Callback body the worker POSTs when a re-embed fails.</summary>
public record VoiceprintFailure(Guid VoiceSampleId, string Error);

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
    IReadOnlyList<Guid> DeleteRecordingIds,
    /// <summary><c>"recordings"</c> (default) folds whole recordings together; <c>"live-chunks"</c>
    /// concatenates slices of one live capture. The worker uses it to decide whether to byte-join
    /// first - live chunks are not individually decodable - and echoes it back so the API can decide
    /// what the finished merge means.</summary>
    string Kind = "recordings");

/// <summary>Callback body the worker POSTs when the concatenated audio is ready.</summary>
public record AudioMergeResult(
    Guid RecordingId,
    string BlobKey,
    string ContentType,
    long SizeBytes,
    long DurationMs,
    IReadOnlyList<Guid> DeleteRecordingIds,
    string Kind = "recordings");

/// <summary>Callback body the worker POSTs when the merge fails (originals are kept).</summary>
public record AudioMergeFailure(
    Guid RecordingId,
    string Error);
