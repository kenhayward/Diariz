using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

public interface IJobQueue
{
    Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default);
    Task EnqueueSummarizationAsync(SummarizationJob job, CancellationToken ct = default);
    Task EnqueueMeetingMinutesAsync(MeetingMinutesJob job, CancellationToken ct = default);
    Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default);
}

/// <summary>Producer side of the transcription + summarisation queues, backed by Redis Streams.</summary>
public class RedisJobQueue : IJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JobQueueOptions _opts;
    private readonly SummarizationOptions _summaryOpts;
    private readonly MeetingMinutesOptions _minutesOpts;

    public RedisJobQueue(IConnectionMultiplexer redis, IOptions<JobQueueOptions> opts,
        IOptions<SummarizationOptions> summaryOpts, IOptions<MeetingMinutesOptions> minutesOpts)
    {
        _redis = redis;
        _opts = opts.Value;
        _summaryOpts = summaryOpts.Value;
        _minutesOpts = minutesOpts.Value;
    }

    public async Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_opts.StreamKey, "job", payload);
    }

    public async Task EnqueueSummarizationAsync(SummarizationJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_summaryOpts.StreamKey, "job", payload);
    }

    public async Task EnqueueMeetingMinutesAsync(MeetingMinutesJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_minutesOpts.StreamKey, "job", payload);
    }

    public async Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_opts.MergeStreamKey, "job", payload);
    }
}
