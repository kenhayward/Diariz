using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// The live-chunk job's wire format, on real Redis.
/// <para>
/// This cannot be written any other way. The payload is produced by .NET and parsed by Python, and
/// nothing in the C# tests pairs the key this side writes with the one <c>worker.handle_live_chunk</c>
/// reads. A round-trip through <c>JsonSerializer</c> proves nothing: it passes just as happily if both
/// sides agree on the wrong name. So these assert the literal key text.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LiveChunkQueueIntegrationTests(ContainersFixture fx)
{
    private static RedisJobQueue Queue(IConnectionMultiplexer mux, string streamKey) =>
        new(mux, Options.Create(new JobQueueOptions { LiveChunkStreamKey = streamKey }),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()),
            Options.Create(new SectionMinutesOptions()), Options.Create(new FormulaRunOptions()));

    private static LiveChunkJob Sample(string? prev = "u/r/chunks/00002.webm") =>
        new(Guid.NewGuid(), Guid.NewGuid(), 3, "u/r/chunks/00003.webm", prev, 90_000, 3_000, "en");

    [Fact]
    public async Task LiveChunkJob_UsesThePascalCaseKeysTheWorkerReads()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"live-chunk-{Guid.NewGuid()}";
        var job = Sample();

        await Queue(mux, streamKey).EnqueueLiveChunkAsync(job);

        var entry = Assert.Single(await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+"));
        var json = entry.Values.Single(v => v.Name == "job").Value.ToString();

        // Every key worker.handle_live_chunk does job["..."] / job.get("...") on. A rename on either
        // side breaks the feature silently, and only this assertion notices.
        foreach (var key in new[]
                 {
                     "RecordingId", "TranscriptionId", "Sequence", "BlobKey",
                     "PrevBlobKey", "OffsetMs", "OverlapMs", "Language",
                 })
        {
            Assert.Contains($"\"{key}\"", json);
        }

        var roundTripped = JsonSerializer.Deserialize<LiveChunkJob>(json)!;
        Assert.Equal(job.Sequence, roundTripped.Sequence);
        Assert.Equal(job.BlobKey, roundTripped.BlobKey);
        Assert.Equal(job.PrevBlobKey, roundTripped.PrevBlobKey);
        Assert.Equal(90_000, roundTripped.OffsetMs);
        Assert.Equal(3_000, roundTripped.OverlapMs);
    }

    [Fact]
    public async Task TheFirstChunk_CarriesANullPrevBlobKey_RatherThanOmittingIt()
    {
        // Sequence 0 has nothing before it. The key must still be present and null: Python reads it with
        // job.get("PrevBlobKey"), and a missing key and a null one behave the same there - but a
        // serializer configured to drop nulls would also drop keys the worker needs elsewhere, so pin
        // the shape rather than relying on that staying true.
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"live-chunk-{Guid.NewGuid()}";

        await Queue(mux, streamKey).EnqueueLiveChunkAsync(Sample(prev: null) with { Sequence = 0, OffsetMs = 0 });

        var entry = Assert.Single(await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+"));
        var json = entry.Values.Single(v => v.Name == "job").Value.ToString();

        Assert.Contains("\"PrevBlobKey\"", json);
        Assert.Null(JsonSerializer.Deserialize<LiveChunkJob>(json)!.PrevBlobKey);
    }

    [Fact]
    public async Task LiveChunkJobs_GoToTheirOwnStream_NotTheTranscriptionOne()
    {
        // The whole point of a separate stream is that the worker can prefer it over a queued
        // full-meeting transcription. Sharing one would make that impossible.
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var liveKey = $"live-chunk-{Guid.NewGuid()}";
        var transcriptionKey = $"jobs-{Guid.NewGuid()}";
        var queue = new RedisJobQueue(mux,
            Options.Create(new JobQueueOptions { LiveChunkStreamKey = liveKey, StreamKey = transcriptionKey }),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()),
            Options.Create(new SectionMinutesOptions()), Options.Create(new FormulaRunOptions()));

        await queue.EnqueueLiveChunkAsync(Sample());

        Assert.Single(await mux.GetDatabase().StreamRangeAsync(liveKey, "-", "+"));
        Assert.Empty(await mux.GetDatabase().StreamRangeAsync(transcriptionKey, "-", "+"));
    }
}
