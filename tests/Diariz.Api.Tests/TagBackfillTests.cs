using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class TagBackfillTests
{
    private static Transcription AddTranscription(DiarizDbContext db, Guid recId, int version)
    {
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = version };
        db.Transcriptions.Add(tr);
        return tr;
    }

    [Fact]
    public async Task Run_EnqueuesLatestTranscription_ForRecordingsNeverTagged()
    {
        using var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" };
        db.Recordings.Add(rec);
        AddTranscription(db, rec.Id, version: 1);
        var latest = AddTranscription(db, rec.Id, version: 2);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var count = await TagBackfill.RunAsync(db, queue, NullLogger.Instance);

        Assert.Equal(1, count);
        var job = Assert.Single(queue.TagsEnqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(latest.Id, job.TranscriptionId); // the latest version, not v1
    }

    [Fact]
    public async Task Run_SkipsRecordings_AlreadyTagged()
    {
        using var db = TestDb.Create();
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k",
            TagsExtractedAt = DateTimeOffset.UtcNow, // even a zero-tag extraction counts as done
        };
        db.Recordings.Add(rec);
        AddTranscription(db, rec.Id, version: 1);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var count = await TagBackfill.RunAsync(db, queue, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Empty(queue.TagsEnqueued);
    }

    [Fact]
    public async Task Run_SkipsRecordings_WithNoTranscription()
    {
        using var db = TestDb.Create();
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" });
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var count = await TagBackfill.RunAsync(db, queue, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Empty(queue.TagsEnqueued);
    }
}
