using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RedisJobQueueIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task EnqueueAsync_AddsJsonJobToTheStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var opts = Options.Create(new JobQueueOptions { StreamKey = $"jobs-{Guid.NewGuid()}" });
        var queue = new RedisJobQueue(mux, opts, Options.Create(new SummarizationOptions()),
            Options.Create(new MeetingMinutesOptions()), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new TranscriptionJob(Guid.NewGuid(), Guid.NewGuid(), "user/blob.webm", "whisperx-large-v3");
        await queue.EnqueueAsync(job);

        // The Python worker reads this exact shape: one stream entry with a PascalCase-JSON "job" field.
        var entries = await mux.GetDatabase().StreamRangeAsync(opts.Value.StreamKey, "-", "+");
        var entry = Assert.Single(entries);
        var jobField = entry.Values.Single(v => v.Name == "job");
        var json = jobField.Value.ToString();

        var roundTripped = JsonSerializer.Deserialize<TranscriptionJob>(json);
        Assert.Equal(job.TranscriptionId, roundTripped!.TranscriptionId);
        Assert.Equal(job.RecordingId, roundTripped.RecordingId);
        Assert.Equal("user/blob.webm", roundTripped.BlobKey);
        Assert.Equal("whisperx-large-v3", roundTripped.Model);

        // Guard the wire contract: keys must stay PascalCase or the worker breaks.
        Assert.Contains("\"BlobKey\"", json);
    }

    [Fact]
    public async Task EnqueueSummarizationAsync_AddsJsonJobToSummarizationStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"sum-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions { StreamKey = streamKey }),
            Options.Create(new MeetingMinutesOptions()), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new SummarizationJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueSummarizationAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<SummarizationJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }

    [Fact]
    public async Task EnqueueMeetingMinutesAsync_AddsJsonJobToMinutesStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"minutes-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions()),
            Options.Create(new MeetingMinutesOptions { StreamKey = streamKey }), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new MeetingMinutesJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueMeetingMinutesAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<MeetingMinutesJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }

    [Fact]
    public async Task EnqueueActionsAsync_AddsJsonJobToActionsStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"actions-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions { StreamKey = streamKey }), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new ActionsJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueActionsAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<ActionsJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }

    [Fact]
    public async Task EnqueueEmbeddingAsync_AddsJsonJobToEmbeddingStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"embed-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions { StreamKey = streamKey }),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new EmbeddingJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueEmbeddingAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<EmbeddingJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }

    [Fact]
    public async Task EnqueueTagsAsync_AddsJsonJobToTagsStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"tags-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions { StreamKey = streamKey }), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()));

        var job = new TagsJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueTagsAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<TagsJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }
}
