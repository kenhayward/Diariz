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

/// <summary>Stub <see cref="ISummarizationClient"/> — returns a canned result or throws.</summary>
public sealed class FakeSummarizationClient : ISummarizationClient
{
    public SummaryResult Result { get; set; } = new("A concise summary.", "Auto Name");
    public Exception? ThrowOnCall { get; set; }
    public int Calls { get; private set; }
    public bool LastNeedName { get; private set; }

    public Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<SegmentDto> segments, bool needName, CancellationToken ct = default)
    {
        Calls++;
        LastNeedName = needName;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        return Task.FromResult(Result);
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
    public string PresignedUrl { get; set; } = "https://example.test/audio";
    /// <summary>Captures the download filename requested on the last presign call (null = inline).</summary>
    public string? LastPresignDownloadFileName { get; private set; }

    public Task EnsureBucketAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[key] = ms.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(Objects[key]));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }

    public string GetPresignedDownloadUrl(string key, TimeSpan expiry, string? downloadFileName = null)
    {
        LastPresignDownloadFileName = downloadFileName;
        return PresignedUrl;
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
