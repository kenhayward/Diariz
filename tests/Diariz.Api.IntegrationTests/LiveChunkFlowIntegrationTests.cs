using System.Text;
using System.Text.Json;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// The parts of live capture that the in-memory provider and the fakes cannot see: real blobs in
/// MinIO, the real Redis wire format the Python worker parses, and real cascade behaviour.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LiveChunkFlowIntegrationTests(ContainersFixture fx)
{
    private async Task<IAudioStorage> StorageAsync()
    {
        var s3 = new AmazonS3Client(fx.MinioAccessKey, fx.MinioSecretKey, new AmazonS3Config
        {
            ServiceURL = fx.MinioEndpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
        var bucket = $"live-{Guid.NewGuid():N}";
        var storage = new AudioStorage(s3, Options.Create(new StorageOptions { Bucket = bucket }));
        await storage.EnsureBucketAsync();
        return storage;
    }

    private static RedisJobQueue Queue(IConnectionMultiplexer mux, string streamKey) =>
        new(mux, Options.Create(new JobQueueOptions { MergeStreamKey = streamKey }),
            Options.Create(new SummarizationOptions()), Options.Create(new MeetingMinutesOptions()),
            Options.Create(new ActionsOptions()), Options.Create(new EmbeddingOptions()),
            Options.Create(new TagsOptions()), Options.Create(new SectionSummaryOptions()),
            Options.Create(new SectionMinutesOptions()), Options.Create(new FormulaRunOptions()));

    [Fact]
    public async Task Chunks_RoundTripThroughRealObjectStorage_InSequenceOrder()
    {
        var storage = await StorageAsync();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            var key = $"{userId}/{recordingId}/chunks/{i:D5}.webm";
            using var body = new MemoryStream(Encoding.UTF8.GetBytes($"chunk-{i}"));
            await storage.UploadAsync(key, body, "audio/webm");
        }

        for (var i = 0; i < 3; i++)
        {
            var key = $"{userId}/{recordingId}/chunks/{i:D5}.webm";
            await using var read = await storage.OpenReadAsync(key);
            using var reader = new StreamReader(read);
            Assert.Equal($"chunk-{i}", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task LiveMergeJob_CarriesKindAndKeysUnderThePascalCaseNamesTheWorkerReads()
    {
        // The payload is produced by .NET and parsed by Python. Nothing in the C# tests pairs the key
        // this side writes with the one worker.handle_merge reads (job.get("Kind")), so a rename on
        // either side would silently send every live capture down the whole-recordings path - which
        // fails on the second chunk with "EBML header parsing failed".
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"audio-merge-{Guid.NewGuid()}";
        var queue = Queue(mux, streamKey);
        var recordingId = Guid.NewGuid();
        string[] keys = ["u/r/chunks/00000.webm", "u/r/chunks/00001.webm", "u/r/chunks/00002.webm"];

        await queue.EnqueueAudioMergeAsync(new AudioMergeJob(
            recordingId, keys, "u/r-live.webm", [], Kind: "live-chunks"));

        var entry = Assert.Single(await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+"));
        var json = entry.Values.Single(v => v.Name == "job").Value.ToString();

        Assert.Contains("\"Kind\"", json);
        Assert.Contains("\"BlobKeys\"", json);
        var roundTripped = JsonSerializer.Deserialize<AudioMergeJob>(json)!;
        Assert.Equal("live-chunks", roundTripped.Kind);
        Assert.Equal(keys, roundTripped.BlobKeys);   // order is the capture order
        Assert.Empty(roundTripped.DeleteRecordingIds);
    }

    [Fact]
    public async Task AnOrdinaryMergeJob_StillDefaultsToTheRecordingsKind()
    {
        // The pre-existing path must keep working without the API being changed to pass Kind.
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var streamKey = $"audio-merge-{Guid.NewGuid()}";

        await Queue(mux, streamKey).EnqueueAudioMergeAsync(new AudioMergeJob(
            Guid.NewGuid(), ["u/a.webm", "u/b.webm"], "u/merged.webm", [Guid.NewGuid()]));

        var entry = Assert.Single(await mux.GetDatabase().StreamRangeAsync(streamKey, "-", "+"));
        var json = entry.Values.Single(v => v.Name == "job").Value.ToString();
        Assert.Equal("recordings", JsonSerializer.Deserialize<AudioMergeJob>(json)!.Kind);
    }

    [Fact]
    public async Task ChunkSizes_SumIntoTheRecordingRow_UnderRealRelationalConstraints()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{Guid.NewGuid()}@x.test",
            Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = user.Id, Title = "Live", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        db.Recordings.Add(rec);
        for (var i = 0; i < 4; i++)
            db.RecordingChunks.Add(new RecordingChunk
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Sequence = i,
                BlobKey = $"{user.Id}/{rec.Id}/chunks/{i:D5}.webm",
                StartMs = i * 30_000, EndMs = (i + 1) * 30_000, SizeBytes = 1000 + i,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var total = await db.RecordingChunks.Where(c => c.RecordingId == rec.Id).SumAsync(c => c.SizeBytes);
        Assert.Equal(4006, total);

        // Ordering is by Sequence, not by insertion or by key - the finalise job depends on it.
        var ordered = await db.RecordingChunks
            .Where(c => c.RecordingId == rec.Id)
            .OrderBy(c => c.Sequence)
            .Select(c => c.Sequence)
            .ToListAsync();
        Assert.Equal([0, 1, 2, 3], ordered);
    }
}
