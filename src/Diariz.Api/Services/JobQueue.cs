using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

public interface IJobQueue
{
    Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default);
}

/// <summary>Producer side of the transcription queue, backed by a Redis Stream.</summary>
public class RedisJobQueue : IJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JobQueueOptions _opts;

    public RedisJobQueue(IConnectionMultiplexer redis, IOptions<JobQueueOptions> opts)
    {
        _redis = redis;
        _opts = opts.Value;
    }

    public async Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_opts.StreamKey, "job", payload);
    }
}
