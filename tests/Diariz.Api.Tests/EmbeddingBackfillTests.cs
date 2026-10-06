using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class EmbeddingBackfillTests
{
    private static Transcription AddTranscription(DiarizDbContext db, Guid recId, int version)
    {
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = version };
        db.Transcriptions.Add(tr);
        return tr;
    }

    [Fact]
    public async Task Run_EnqueuesLatestTranscription_ForRecordingsWithoutChunks()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        db.Recordings.Add(rec);
        AddTranscription(db, rec.Id, version: 1);
        var latest = AddTranscription(db, rec.Id, version: 2);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var count = await EmbeddingBackfill.RunAsync(db, queue, NullLogger.Instance);

        Assert.Equal(1, count);
        var job = Assert.Single(queue.EmbeddingEnqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(latest.Id, job.TranscriptionId); // the latest version, not v1
    }

    [Fact]
    public async Task Run_SkipsRecordings_ThatAlreadyHaveChunks()
    {
        using var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" };
        db.Recordings.Add(rec);
        var tr = AddTranscription(db, rec.Id, version: 1);
        db.TranscriptChunks.Add(new TranscriptChunk
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, RecordingId = rec.Id, UserId = rec.UserId,
            Ordinal = 0, Text = "already indexed",
        });
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var count = await EmbeddingBackfill.RunAsync(db, queue, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Empty(queue.EmbeddingEnqueued);
    }
}

/// <summary>When the startup backfill runs at all. It used to look only at the server's env endpoints, so an
/// endpoint saved on the AI models page (issue #836) - or one borrowed from the default model - never got a
/// library indexed after a restart.</summary>
public class EmbeddingBackfillServiceTests
{
    private static async Task<FakeJobQueue> RunAsync(EmbeddingRequestConfig config)
    {
        var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 });
        db.SaveChanges();

        var queue = new FakeJobQueue();
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IJobQueue>(queue)
            .AddSingleton<IEmbeddingSettingsResolver>(new FakeEmbeddingSettingsResolver { Config = config })
            .BuildServiceProvider();

        var service = new EmbeddingBackfillService(services, NullLogger<EmbeddingBackfillService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        return queue;
    }

    [Fact]
    public async Task Runs_WhenAnyLevelResolvesAnEndpoint()
    {
        var queue = await RunAsync(new EmbeddingRequestConfig("http://page.test/v1", "", "m", 768, 60, 32)
        {
            Source = EmbeddingEndpointSource.Platform,
        });

        Assert.Single(queue.EmbeddingEnqueued);
    }

    [Fact]
    public async Task Skips_WhenNoEndpointResolves()
    {
        var queue = await RunAsync(new EmbeddingRequestConfig("", "", "m", 768, 60, 32));

        Assert.Empty(queue.EmbeddingEnqueued);
    }
}
