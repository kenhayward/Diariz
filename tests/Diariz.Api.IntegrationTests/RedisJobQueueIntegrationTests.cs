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
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

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

    /// <summary>The pinned language crosses the .NET -> Python boundary by name: nothing else pairs the
    /// key this side writes with the one <c>worker.handle</c> reads (<c>job.get("Language")</c>), so a
    /// rename on either side would silently go back to auto-detecting every recording.</summary>
    [Fact]
    public async Task EnqueueAsync_CarriesThePinnedLanguageUnderThePascalCaseKeyTheWorkerReads()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var opts = Options.Create(new JobQueueOptions { StreamKey = $"jobs-{Guid.NewGuid()}" });
        var queue = new RedisJobQueue(mux, opts, Options.Create(new SummarizationOptions()),
            Options.Create(new MeetingMinutesOptions()), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

        await queue.EnqueueAsync(new TranscriptionJob(
            Guid.NewGuid(), Guid.NewGuid(), "user/blob.webm", "whisperx-large-v3", Language: "pt"));

        var entries = await mux.GetDatabase().StreamRangeAsync(opts.Value.StreamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();

        Assert.Contains("\"Language\":\"pt\"", json);
    }

    [Fact]
    public async Task EnqueueSummarizationAsync_AddsJsonJobToSummarizationStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"sum-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions { StreamKey = streamKey }),
            Options.Create(new MeetingMinutesOptions()), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

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
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

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
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

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
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

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
            Options.Create(new TagsOptions { StreamKey = streamKey }), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

        var job = new TagsJob(Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueTagsAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<TagsJob>(json);
        Assert.Equal(job.RecordingId, roundTripped!.RecordingId);
        Assert.Equal(job.TranscriptionId, roundTripped.TranscriptionId);
    }

    [Fact]
    public async Task EnqueueFormulaRunAsync_AddsJsonJobToFormulaRunStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"formula-run-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux, Options.Create(new JobQueueOptions()),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()),
            Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions { StreamKey = streamKey }));

        var job = new FormulaRunJob(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueFormulaRunAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();
        var roundTripped = JsonSerializer.Deserialize<FormulaRunJob>(json);
        Assert.Equal(job, roundTripped);
    }

    /// <summary>The voiceprint re-embed contract crosses the .NET -> Python boundary by name. Nothing else
    /// pairs the keys this side writes with the ones <c>worker.handle_voiceprint</c> reads, so a rename on
    /// either side is silent until a real job runs and fails on a KeyError.</summary>
    [Fact]
    public async Task EnqueueVoiceprintAsync_WritesThePascalCaseShapeTheWorkerReads()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var opts = Options.Create(new JobQueueOptions { VoiceprintStreamKey = $"voiceprint-{Guid.NewGuid()}" });
        var queue = new RedisJobQueue(mux, opts, Options.Create(new SummarizationOptions()),
            Options.Create(new MeetingMinutesOptions()), Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()), Options.Create(new SectionMinutesOptions()),
            Options.Create(new FormulaRunOptions()));

        var job = new VoiceprintJob(Guid.NewGuid(), Guid.NewGuid(), "user/blob.webm",
            [new VoiceprintSpan(1000, 3000), new VoiceprintSpan(5000, 6000)]);
        await queue.EnqueueVoiceprintAsync(job);

        var entries = await mux.GetDatabase().StreamRangeAsync(opts.Value.VoiceprintStreamKey, "-", "+");
        var json = Assert.Single(entries).Values.Single(v => v.Name == "job").Value.ToString();

        var roundTripped = JsonSerializer.Deserialize<VoiceprintJob>(json);
        Assert.Equal(job.VoiceSampleId, roundTripped!.VoiceSampleId);
        Assert.Equal("user/blob.webm", roundTripped.BlobKey);
        Assert.Equal(job.Spans, roundTripped.Spans);

        // Every key the worker indexes by name.
        Assert.Contains("\"VoiceSampleId\"", json);
        Assert.Contains("\"BlobKey\"", json);
        Assert.Contains("\"Spans\"", json);
        Assert.Contains("\"StartMs\"", json);
        Assert.Contains("\"EndMs\"", json);
    }

    /// <summary>The voiceprint stream must be its own key. Sharing one with transcription would hand a
    /// re-embed to <c>worker.handle</c>, which would try to transcribe it.</summary>
    [Fact]
    public void VoiceprintStreamKey_DefaultsToItsOwnStream()
    {
        var opts = new JobQueueOptions();
        Assert.Equal("voiceprint-jobs", opts.VoiceprintStreamKey);
        Assert.NotEqual(opts.StreamKey, opts.VoiceprintStreamKey);
        Assert.NotEqual(opts.MergeStreamKey, opts.VoiceprintStreamKey);
    }
}
