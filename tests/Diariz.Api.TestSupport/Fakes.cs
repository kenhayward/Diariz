using System.Net;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Canned <see cref="HttpMessageHandler"/> that records the outgoing request so tests
/// can assert the URL/headers/body and control the response.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _status;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_status) { Content = new StringContent(_responseBody) };
    }
}

/// <summary>Canned <see cref="HttpMessageHandler"/> that dequeues one response body per request (the last
/// repeats once drained) and records every outgoing request body, so tests can assert batched calls.</summary>
public sealed class QueuedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<string> _responses;
    private string _last = "{}";
    public List<string> Requests { get; } = new();

    public QueuedHttpMessageHandler(Queue<string> responses) => _responses = responses;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (_responses.Count > 0) _last = _responses.Dequeue();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_last) };
    }
}

/// <summary>Test double for <see cref="IPlatformSettingsService"/> that returns the seeded singleton
/// <see cref="PlatformSettings"/> row from the given <see cref="DiarizDbContext"/>, rather than lazily
/// creating one - lets a test control <c>ApiAccessEnabled</c> (and other flags) up front.</summary>
public sealed class FixedPlatformSettings(Diariz.Domain.DiarizDbContext db) : IPlatformSettingsService
{
    public Task<PlatformSettings> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(db.PlatformSettings.First());
}

/// <summary>Returns a fixed summarisation config and records the resolved user id.</summary>
public sealed class FakeSummarizationSettingsResolver : ISummarizationSettingsResolver
{
    public SummarizationRequestConfig Config { get; set; } =
        new("https://llm.test/v1", "sk-test", "test-model", 60);
    public Guid? LastUserId { get; private set; }

    public Task<SummarizationRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        LastUserId = userId;
        return Task.FromResult(Config);
    }
}

/// <summary>Reversible stand-in for the Data Protection key protector (prefixes instead of encrypts).</summary>
public sealed class FakeApiKeyProtector : IApiKeyProtector
{
    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : "enc:" + plaintext;

    public string? Unprotect(string? ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? null
        : ciphertext.StartsWith("enc:") ? ciphertext["enc:".Length..]
        : null;
}

/// <summary>Stub <see cref="ISummarizationClient"/> — returns a canned result or throws.</summary>
public sealed class FakeSummarizationClient : ISummarizationClient
{
    public SummaryResult Result { get; set; } = new("A concise summary.", "Auto Name");
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public bool LastNeedName { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public string? LastTemplate { get; private set; }

    public Task<SummaryResult> SummarizeAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, bool needName, string template,
        CancellationToken ct = default)
    {
        Calls++;
        LastNeedName = needName;
        LastConfig = config;
        LastTemplate = template;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(Result);
    }
}

/// <summary>Stub <see cref="IMeetingMinutesClient"/> — returns canned Markdown or throws, and records the
/// arguments it was called with.</summary>
public sealed class FakeMeetingMinutesClient : IMeetingMinutesClient
{
    public string Result { get; set; } = "# Meeting\n\nMinutes body.";
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    /// <summary>Every call's messages, in call order (useful when a strategy makes several calls).</summary>
    public List<IReadOnlyList<ChatMessage>> AllMessages { get; } = new();

    /// <summary>Optional per-call response; when null, every call returns <see cref="Result"/>. Lets a test give
    /// each prompt block a distinct answer (to assert ordering/assembly).</summary>
    public Func<IReadOnlyList<ChatMessage>, string>? Responder { get; set; }

    public Task<string> GenerateAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastMessages = messages;
        AllMessages.Add(messages);
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(Responder?.Invoke(messages) ?? Result);
    }
}

/// <summary>Stub <see cref="IMeetingTypeMinutesGenerator"/> - returns canned Markdown (or throws) and records the
/// meeting-type + actions it was handed, so the processor can be tested without the real generator/strategies.</summary>
public sealed class FakeMeetingTypeMinutesGenerator : IMeetingTypeMinutesGenerator
{
    public string Result { get; set; } = "# Minutes\n\nBody.";
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public Guid LastOwnerId { get; private set; }
    public Guid? LastMeetingTypeId { get; private set; }
    public IReadOnlyList<ExtractedAction>? LastActions { get; private set; }
    public IReadOnlyList<MeetingNoteDto>? LastNotes { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }

    public Task<string> GenerateAsync(
        Guid recordingOwnerId, Guid? meetingTypeId, MeetingMinutesContext context,
        IReadOnlyList<SegmentDto> segments, IReadOnlyList<ExtractedAction> actions,
        IReadOnlyList<MeetingNoteDto> notes,
        SummarizationRequestConfig config, int charBudget, CancellationToken ct = default)
    {
        Calls++;
        LastOwnerId = recordingOwnerId;
        LastMeetingTypeId = meetingTypeId;
        LastActions = actions;
        LastNotes = notes;
        LastConfig = config;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(Result);
    }
}

/// <summary>Stub <see cref="IActionsClient"/> — returns a canned action list or throws.</summary>
public sealed class FakeActionsClient : IActionsClient
{
    public List<ExtractedAction> Result { get; set; } = new();
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public IReadOnlyList<SegmentDto>? LastSegments { get; private set; }
    public string? LastTemplate { get; private set; }
    public DateTimeOffset? LastMeetingDate { get; private set; }

    public Task<IReadOnlyList<ExtractedAction>> ExtractAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        DateTimeOffset? meetingDate, CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastSegments = segments;
        LastTemplate = template;
        LastMeetingDate = meetingDate;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult<IReadOnlyList<ExtractedAction>>(Result);
    }
}

/// <summary>Stub <see cref="ITagsClient"/> — returns a canned tag list or throws.</summary>
public sealed class FakeTagsClient : ITagsClient
{
    public List<ExtractedTag> Result { get; set; } = new();
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public IReadOnlyList<SegmentDto>? LastSegments { get; private set; }
    public string? LastTemplate { get; private set; }

    public Task<IReadOnlyList<ExtractedTag>> ExtractAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastSegments = segments;
        LastTemplate = template;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult<IReadOnlyList<ExtractedTag>>(Result);
    }
}

/// <summary>Stub <see cref="ITranslationClient"/> — by default echoes each input prefixed with the target
/// language (so tests can assert what was translated), or throws.</summary>
public sealed class FakeTranslationClient : ITranslationClient
{
    /// <summary>Optional override: maps an exact input string to a fixed translation.</summary>
    public Dictionary<string, string> Map { get; } = new();
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public string? LastLanguage { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }

    public Task<IReadOnlyList<string>> TranslateAsync(
        SummarizationRequestConfig config, string targetLanguage, IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        Calls++;
        LastLanguage = targetLanguage;
        LastConfig = config;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        var result = texts
            .Select(t => string.IsNullOrWhiteSpace(t) ? t : Map.TryGetValue(t, out var m) ? m : $"[{targetLanguage}] {t}")
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}

/// <summary>Returns a fixed embedding config and records the resolved user id.</summary>
public sealed class FakeEmbeddingSettingsResolver : IEmbeddingSettingsResolver
{
    public EmbeddingRequestConfig Config { get; set; } =
        new("https://emb.test/v1", "sk-emb", "nomic-embed-text", 768, 60, 32)
        {
            QueryPrefix = "search_query: ",
            DocumentPrefix = "search_document: ",
        };
    public Guid? LastUserId { get; private set; }

    public Task<EmbeddingRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        LastUserId = userId;
        return Task.FromResult(Config);
    }
}

/// <summary>Stub <see cref="IEmbeddingClient"/> — returns a deterministic unit vector per input (unless
/// <see cref="Vectors"/> overrides) and records the inputs it was asked to embed.</summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    /// <summary>Optional exact outputs (one per input). When null, a fixed 3-d vector is returned per input.</summary>
    public IReadOnlyList<float[]>? Vectors { get; set; }
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public EmbeddingRequestConfig? LastConfig { get; private set; }
    public IReadOnlyList<string>? LastInputs { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingRequestConfig config, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastInputs = inputs.ToList();
        if (ThrowOnCall is not null) throw ThrowOnCall;
        var result = Vectors ?? inputs.Select((_, i) => new[] { 1f, 0f, (float)i }).ToList();
        return Task.FromResult(result);
    }
}

/// <summary>Records emails it was asked to send; <see cref="Sent"/> toggles the return value to
/// simulate "SMTP configured" (true) vs the unconfigured fallback (false).</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public bool Sent { get; set; } = true;
    public List<(string To, string Subject, string Body)> Messages { get; } = new();
    /// <summary>Attachments passed on the most recent send (empty when none).</summary>
    public List<EmailAttachment> LastAttachments { get; private set; } = new();

    public Task<bool> SendAsync(string to, string subject, string htmlBody,
        IEnumerable<EmailAttachment>? attachments = null, CancellationToken ct = default)
    {
        Messages.Add((to, subject, htmlBody));
        LastAttachments = attachments?.ToList() ?? new();
        return Task.FromResult(Sent);
    }
}

/// <summary>Stub <see cref="ISpeakerIdentifier"/> — returns a canned match (or none) and records the
/// embeddings it was asked about, so the callback's auto-identification can be unit-tested without pgvector.</summary>
public sealed class FakeSpeakerIdentifier : ISpeakerIdentifier
{
    public SpeakerMatch? Match { get; set; }
    public int Calls { get; private set; }

    public Task<SpeakerMatch?> IdentifyAsync(Guid userId, Pgvector.Vector embedding, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Match);
    }
}

/// <summary>Stub <see cref="IChatStreamClient"/> — yields a canned token sequence or throws. For the
/// tool-calling path, <see cref="ChunkRounds"/> scripts one delta list per model call (sequentially); when
/// empty, <see cref="StreamChunksAsync"/> falls back to streaming <see cref="Tokens"/> as content deltas.</summary>
public sealed class FakeChatStreamClient : IChatStreamClient
{
    public List<string> Tokens { get; set; } = ["Project", " Kickoff", " Recap"];
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public List<ChatMessage>? LastMessages { get; private set; }

    /// <summary>Scripted token output per <see cref="StreamAsync"/> call, joined to form that call's completion
    /// (the last entry repeats once drained). When empty, every call streams <see cref="Tokens"/>. Lets a
    /// map-reduce test give each call a distinct answer (to assert which output the row ended up with).</summary>
    public List<string> StreamRounds { get; set; } = new();
    /// <summary>The messages passed to each <see cref="StreamAsync"/> call, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> AllStreamMessages { get; } = new();
    private int _streamCall;

    /// <summary>Scripted output per <see cref="StreamChunksAsync"/> call (the last entry repeats).</summary>
    public List<List<ChatStreamDelta>> ChunkRounds { get; set; } = new();
    /// <summary>The messages passed to each <see cref="StreamChunksAsync"/> call.</summary>
    public List<IReadOnlyList<object>> ChunkCallMessages { get; } = new();
    /// <summary>The tool specs passed to each <see cref="StreamChunksAsync"/> call (null = none offered).</summary>
    public List<IReadOnlyList<object>?> ChunkCallTools { get; } = new();
    private int _chunkCall;

    public async IAsyncEnumerable<string> StreamAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastMessages = messages.ToList();
        AllStreamMessages.Add(messages.ToList());
        var round = _streamCall;
        _streamCall++;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        var tokens = StreamRounds.Count > 0
            ? new List<string> { StreamRounds[Math.Min(round, StreamRounds.Count - 1)] }
            : Tokens;
        foreach (var t in tokens)
        {
            ct.ThrowIfCancellationRequested();
            yield return t;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<ChatStreamDelta> StreamChunksAsync(
        SummarizationRequestConfig config, IReadOnlyList<object> messages,
        IReadOnlyList<object>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        ChunkCallMessages.Add(messages.ToList());
        ChunkCallTools.Add(tools);
        // Mirror the pre-shaped {role, content} objects into LastMessages so tests can inspect the prompt
        // the same way they do for StreamAsync.
        LastMessages = messages.Select(ToChatMessage).ToList();
        if (ThrowOnCall is not null) throw ThrowOnCall;

        var deltas = ChunkRounds.Count > 0
            ? ChunkRounds[Math.Min(_chunkCall, ChunkRounds.Count - 1)]
            : Tokens.Select(t => new ChatStreamDelta(t, null, null)).ToList();
        _chunkCall++;

        foreach (var d in deltas)
        {
            ct.ThrowIfCancellationRequested();
            yield return d;
            await Task.Yield();
        }
    }

    /// <summary>Reads role/content off a pre-shaped message object (anonymous { role, content }).</summary>
    private static ChatMessage ToChatMessage(object m)
    {
        var t = m.GetType();
        var role = t.GetProperty("role")?.GetValue(m) as string ?? "";
        var content = t.GetProperty("content")?.GetValue(m) as string ?? "";
        return new ChatMessage(role, content);
    }
}

/// <summary>Stub <see cref="IChatToolSettingsResolver"/> — returns a fixed set of active tools (none by
/// default), so the chat controller behaves as the no-tools path unless a test opts in.</summary>
public sealed class FakeChatToolSettingsResolver : IChatToolSettingsResolver
{
    public List<Diariz.Api.Tools.IChatTool> ActiveTools { get; set; } = new();
    public bool MasterEnabled { get; set; }

    public Task<ChatToolSettings> ResolveAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(new ChatToolSettings(MasterEnabled, ActiveTools, []));
}

/// <summary>Stub <see cref="ITranscriptSearch"/> — returns canned results and records the arguments each
/// tool passed, so the tools can be unit-tested without a database.</summary>
public sealed class FakeTranscriptSearch : ITranscriptSearch
{
    public List<TranscriptHit> Hits { get; set; } = new();
    public List<RecordingHit> Recordings { get; set; } = new();
    public List<SpeakerCount> Counts { get; set; } = new();
    public List<SpeakerDuration> TalkTime { get; set; } = new();

    public (Guid UserId, string Phrase, string? Speaker, IReadOnlyList<Guid>? Scope, int Limit, Guid? RoomId)? LastSearch { get; private set; }
    /// <summary>How many times the engine was actually asked. Lets a test assert the caller *short-circuited*
    /// rather than searching - "returned nothing" and "never searched" are very different bugs.</summary>
    public int SearchCalls { get; private set; }
    public (Guid UserId, DateTimeOffset? From, DateTimeOffset? To, string? Name, string? Speaker, string? Contains, int Limit)? LastList { get; private set; }
    public (Guid UserId, string Phrase, string? Speaker, IReadOnlyList<Guid>? Scope)? LastCount { get; private set; }
    public (Guid UserId, IReadOnlyList<Guid>? Scope)? LastTalkTime { get; private set; }

    public Task<IReadOnlyList<TranscriptHit>> SearchAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, int limit, Guid? roomId = null, CancellationToken ct = default)
    {
        SearchCalls++;
        LastSearch = (userId, phrase, speakerName, recordingScope, limit, roomId);
        return Task.FromResult<IReadOnlyList<TranscriptHit>>(Hits);
    }

    public Task<IReadOnlyList<RecordingHit>> ListRecordingsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, string? name, string? speaker,
        string? contains, int limit, CancellationToken ct = default)
    {
        LastList = (userId, from, to, name, speaker, contains, limit);
        return Task.FromResult<IReadOnlyList<RecordingHit>>(Recordings);
    }

    public Task<IReadOnlyList<SpeakerCount>> CountMentionsAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default)
    {
        LastCount = (userId, phrase, speakerName, recordingScope);
        return Task.FromResult<IReadOnlyList<SpeakerCount>>(Counts);
    }

    public Task<IReadOnlyList<SpeakerDuration>> SpeakerTalkTimeAsync(
        Guid userId, IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default)
    {
        LastTalkTime = (userId, recordingScope);
        return Task.FromResult<IReadOnlyList<SpeakerDuration>>(TalkTime);
    }
}

/// <summary>Records the jobs that would have been pushed onto the Redis stream.</summary>
public sealed class FakeJobQueue : IJobQueue
{
    public List<TranscriptionJob> Enqueued { get; } = new();
    public List<SummarizationJob> SummarizationEnqueued { get; } = new();
    public List<MeetingMinutesJob> MeetingMinutesEnqueued { get; } = new();
    public List<ActionsJob> ActionsEnqueued { get; } = new();
    public List<AudioMergeJob> AudioMergeEnqueued { get; } = new();
    public List<EmbeddingJob> EmbeddingEnqueued { get; } = new();
    public List<TagsJob> TagsEnqueued { get; } = new();
    public List<SectionSummaryJob> SectionSummaryEnqueued { get; } = new();
    public List<SectionMinutesJob> SectionMinutesEnqueued { get; } = new();
    public List<FormulaRunJob> FormulaRunJobs { get; } = new();

    public Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default)
    {
        Enqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueSummarizationAsync(SummarizationJob job, CancellationToken ct = default)
    {
        SummarizationEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueMeetingMinutesAsync(MeetingMinutesJob job, CancellationToken ct = default)
    {
        MeetingMinutesEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueActionsAsync(ActionsJob job, CancellationToken ct = default)
    {
        ActionsEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default)
    {
        AudioMergeEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueEmbeddingAsync(EmbeddingJob job, CancellationToken ct = default)
    {
        EmbeddingEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueTagsAsync(TagsJob job, CancellationToken ct = default)
    {
        TagsEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueSectionSummaryAsync(SectionSummaryJob job, CancellationToken ct = default)
    {
        SectionSummaryEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueSectionMinutesAsync(SectionMinutesJob job, CancellationToken ct = default)
    {
        SectionMinutesEnqueued.Add(job);
        return Task.CompletedTask;
    }

    public Task EnqueueFormulaRunAsync(FormulaRunJob job, CancellationToken ct = default)
    {
        FormulaRunJobs.Add(job);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory stand-in for MinIO/S3. Records uploads as byte arrays.</summary>
public sealed class FakeAudioStorage : IAudioStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new();

    public Task EnsureBucketAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[key] = ms.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(Objects[key]));

    public Task<AudioBlob?> OpenAsync(string key, long? from = null, long? to = null, CancellationToken ct = default)
    {
        if (!Objects.TryGetValue(key, out var bytes)) return Task.FromResult<AudioBlob?>(null);
        var start = (int)Math.Clamp(from ?? 0, 0, Math.Max(0, bytes.Length - 1));
        var end = (int)Math.Clamp(to ?? bytes.Length - 1, start, bytes.Length - 1);
        var len = end - start + 1;
        return Task.FromResult<AudioBlob?>(new AudioBlob(new MemoryStream(bytes, start, len), len, "audio/webm"));
    }

    public Task<long?> GetSizeAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Objects.TryGetValue(key, out var bytes) ? bytes.Length : (long?)null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>Called as each key is handed to the consumer. Lets a test observe state mid-enumeration -
    /// e.g. sampling backup progress while the archive is still being assembled.</summary>
    public Action<string>? OnKeyListed { get; set; }

    public async IAsyncEnumerable<string> ListKeysAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in Objects.Keys.ToList())
        {
            OnKeyListed?.Invoke(key);
            yield return key;
        }
        await Task.CompletedTask;
    }
}

/// <summary>In-memory stand-in for the Postgres dump/restore. Records calls and round-trips bytes so the
/// MaintenanceController's archive/restore orchestration can be tested without pg_dump/pg_restore.</summary>
public sealed class FakeDatabaseBackup : IDatabaseBackup
{
    public byte[] DumpBytes { get; set; } = System.Text.Encoding.UTF8.GetBytes("FAKE-PG-DUMP");
    public byte[]? RestoredDump { get; private set; }
    public bool DumpCalled { get; private set; }
    public bool RestoreCalled { get; private set; }

    public async Task DumpToAsync(Stream destination, CancellationToken ct = default)
    {
        DumpCalled = true;
        await destination.WriteAsync(DumpBytes, ct);
    }

    public async Task RestoreFromAsync(Stream dump, CancellationToken ct = default)
    {
        RestoreCalled = true;
        using var ms = new MemoryStream();
        await dump.CopyToAsync(ms, ct);
        RestoredDump = ms.ToArray();
    }
}

/// <summary>Fixed schema version for tests (no EF migrations on the in-memory provider).</summary>
public sealed class FakeSchemaVersion(string current = "20260615111923_InitialCreate") : ISchemaVersion
{
    public string Current { get; set; } = current;
    /// <summary>The ordered migration list this "build" knows. Defaults to just the baseline.</summary>
    public List<string> Known { get; set; } = new() { current };
    /// <summary>Set true when <see cref="MigrateToCurrentAsync"/> is called - lets tests assert a forward-migrate ran.</summary>
    public bool Migrated { get; private set; }

    public Task<string> CurrentAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public IReadOnlyList<string> KnownMigrations => Known;
    public Task MigrateToCurrentAsync(CancellationToken ct = default) { Migrated = true; return Task.CompletedTask; }
}

/// <summary>Stub URL fetcher: returns canned text per URL (or null for "blocked/unreachable").</summary>
public sealed class FakeUrlFetcher : IUrlFetcher
{
    public Dictionary<string, string?> Texts { get; } = new();
    public List<string> Requested { get; } = new();

    public Task<string?> FetchTextAsync(string url, CancellationToken ct = default)
    {
        Requested.Add(url);
        return Task.FromResult(Texts.TryGetValue(url, out var text) ? text : null);
    }
}

/// <summary>In-memory <see cref="IDesktopAuthCodeStore"/> for unit tests: deterministic codes
/// (code-1, code-2, …) and one-shot redemption, mirroring the Redis GETDEL contract.</summary>
public sealed class FakeDesktopAuthCodeStore : IDesktopAuthCodeStore
{
    private readonly Dictionary<string, DesktopAuthTicket> _codes = new();
    private int _seq;

    public Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl)
    {
        var code = $"code-{++_seq}";
        _codes[code] = new DesktopAuthTicket(userId, challenge);
        return Task.FromResult(code);
    }

    public Task<DesktopAuthTicket?> RedeemAsync(string code)
    {
        if (code is not null && _codes.Remove(code, out var ticket))
            return Task.FromResult<DesktopAuthTicket?>(ticket);
        return Task.FromResult<DesktopAuthTicket?>(null);
    }
}

/// <summary>Stub <see cref="IFormulaRunner"/> — returns a canned <see cref="FormulaResult"/> or throws a
/// preset exception, and records the arguments it was called with, so <c>FormulasController.Run</c> can be
/// unit-tested without the LLM/context-assembly pipeline.</summary>
public sealed class FakeFormulaRunner : IFormulaRunner
{
    public FormulaResult Result { get; set; } = new()
    {
        Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Name = "Result", Text = "Generated text.",
    };
    /// <summary>The formula <see cref="ValidateRecordingRunAsync"/> returns on the success path (the async
    /// controller reads its Id/Name to seed the pending result row).</summary>
    public Formula ValidatedFormula { get; set; } = new() { Id = Guid.NewGuid(), Name = "Result" };
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public (Guid UserId, Guid RecordingId, Guid FormulaId)? LastCall { get; private set; }
    /// <summary>The (userId, formulaId) of the most recent <see cref="ValidateFormulaRunAccessAsync"/> call
    /// (the section run controller has no recording to record in <see cref="LastCall"/>).</summary>
    public (Guid UserId, Guid FormulaId)? LastFormulaAccessCall { get; private set; }

    public Task<FormulaResult> RunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default)
    {
        Calls++;
        LastCall = (userId, recordingId, formulaId);
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(Result);
    }

    public Task<Formula> ValidateRecordingRunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default)
    {
        Calls++;
        LastCall = (userId, recordingId, formulaId);
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(ValidatedFormula);
    }

    public Task<Formula> ValidateFormulaRunAccessAsync(Guid userId, Guid formulaId, CancellationToken ct = default)
    {
        Calls++;
        LastFormulaAccessCall = (userId, formulaId);
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(ValidatedFormula);
    }
}

/// <summary>
/// No-op <see cref="IHubContext{THub}"/> that records every message sent so tests can assert
/// which SignalR notifications a controller pushed, without a real hub connection.
/// </summary>
public sealed class FakeHubContext : IHubContext<TranscriptionHub>
{
    private readonly FakeHubClients _clients = new();
    public List<SentMessage> Sent => _clients.Sent;

    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new FakeGroupManager();

    public sealed record SentMessage(string? Group, string Method, object?[] Args);

    private sealed class FakeHubClients : IHubClients
    {
        public List<SentMessage> Sent { get; } = new();
        private IClientProxy Proxy(string? group) => new FakeClientProxy(group, Sent);

        public IClientProxy All => Proxy(null);
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => Proxy(null);
        public IClientProxy Client(string connectionId) => Proxy(null);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy(null);
        public IClientProxy Group(string groupName) => Proxy(groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => Proxy(groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy(null);
        public IClientProxy User(string userId) => Proxy(null);
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy(null);
    }

    private sealed class FakeClientProxy(string? group, List<SentMessage> sink) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            sink.Add(new SentMessage(group, method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>Records every publish a controller makes, without touching the database.</summary>
public sealed class CapturingWebhookPublisher : IWebhookPublisher
{
    public readonly List<(string EventType, Guid Owner, object Data, IReadOnlyList<string> Signals, object? PlatformData)> Published = new();
    public Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)
    { Published.Add((eventType, ownerUserId, data, signals ?? Array.Empty<string>(), platformData)); return Task.CompletedTask; }
}
