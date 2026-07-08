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
    Task EnqueueActionsAsync(ActionsJob job, CancellationToken ct = default);
    Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default);
    Task EnqueueEmbeddingAsync(EmbeddingJob job, CancellationToken ct = default);
    Task EnqueueTagsAsync(TagsJob job, CancellationToken ct = default);
}

/// <summary>Producer side of the transcription + summarisation queues, backed by Redis Streams.</summary>
public class RedisJobQueue : IJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JobQueueOptions _opts;
    private readonly SummarizationOptions _summaryOpts;
    private readonly MeetingMinutesOptions _minutesOpts;
    private readonly ActionsOptions _actionsOpts;
    private readonly EmbeddingOptions _embeddingOpts;
    private readonly TagsOptions _tagsOpts;

    public RedisJobQueue(IConnectionMultiplexer redis, IOptions<JobQueueOptions> opts,
        IOptions<SummarizationOptions> summaryOpts, IOptions<MeetingMinutesOptions> minutesOpts,
        IOptions<ActionsOptions> actionsOpts, IOptions<EmbeddingOptions> embeddingOpts,
        IOptions<TagsOptions> tagsOpts)
    {
        _redis = redis;
        _opts = opts.Value;
        _summaryOpts = summaryOpts.Value;
        _minutesOpts = minutesOpts.Value;
        _actionsOpts = actionsOpts.Value;
        _embeddingOpts = embeddingOpts.Value;
        _tagsOpts = tagsOpts.Value;
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

    public async Task EnqueueActionsAsync(ActionsJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_actionsOpts.StreamKey, "job", payload);
    }

    public async Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_opts.MergeStreamKey, "job", payload);
    }

    public async Task EnqueueEmbeddingAsync(EmbeddingJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_embeddingOpts.StreamKey, "job", payload);
    }

    public async Task EnqueueTagsAsync(TagsJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_tagsOpts.StreamKey, "job", payload);
    }
}
