using System.Net;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
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

    public Task<SummaryResult> SummarizeAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, bool needName,
        CancellationToken ct = default)
    {
        Calls++;
        LastNeedName = needName;
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

    public Task<IReadOnlyList<ExtractedAction>> ExtractAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastSegments = segments;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult<IReadOnlyList<ExtractedAction>>(Result);
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

/// <summary>Records emails it was asked to send; <see cref="Sent"/> toggles the return value to
/// simulate "SMTP configured" (true) vs the unconfigured fallback (false).</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public bool Sent { get; set; } = true;
    public List<(string To, string Subject, string Body)> Messages { get; } = new();

    public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        Messages.Add((to, subject, htmlBody));
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

/// <summary>Stub <see cref="IChatStreamClient"/> — yields a canned token sequence or throws.</summary>
public sealed class FakeChatStreamClient : IChatStreamClient
{
    public List<string> Tokens { get; set; } = ["Project", " Kickoff", " Recap"];
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public SummarizationRequestConfig? LastConfig { get; private set; }
    public List<ChatMessage>? LastMessages { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls++;
        LastConfig = config;
        LastMessages = messages.ToList();
        if (ThrowOnCall is not null) throw ThrowOnCall;
        foreach (var t in Tokens)
        {
            ct.ThrowIfCancellationRequested();
            yield return t;
            await Task.Yield();
        }
    }
}

/// <summary>Records the jobs that would have been pushed onto the Redis stream.</summary>
public sealed class FakeJobQueue : IJobQueue
{
    public List<TranscriptionJob> Enqueued { get; } = new();
    public List<SummarizationJob> SummarizationEnqueued { get; } = new();

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
