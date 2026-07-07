using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class AudioRetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);
    private const int RetentionDays = 30;

    // Anything created on/before this instant is past the retention window.
    private static readonly DateTimeOffset Old = Now.AddDays(-RetentionDays).AddDays(-1);
    private static readonly DateTimeOffset Recent = Now.AddDays(-1);

    private static Recording Rec(
        RecordingStatus status = RecordingStatus.Transcribed,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deletedAt = null,
        DateTimeOffset? protectedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            BlobKey = $"u/{Guid.NewGuid():N}.webm",
            SizeBytes = 1000,
            Status = status,
            CreatedAt = createdAt ?? Old,
            AudioDeletedAt = deletedAt,
            AudioProtectedAt = protectedAt,
        };

    private static (DiarizDbContext db, FakeAudioStorage storage) Seed(params Recording[] recs)
    {
        var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        foreach (var r in recs)
        {
            db.Recordings.Add(r);
            storage.Objects[r.BlobKey] = new byte[r.SizeBytes];
        }
        db.SaveChanges();
        return (db, storage);
    }

    [Fact]
    public async Task RunAsync_DeletesEligibleAudio_StampsDeletedAt_AndZeroesSize()
    {
        var rec = Rec();
        var (db, storage) = Seed(rec);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(1, deleted);
        var saved = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(Now, saved!.AudioDeletedAt);
        Assert.Equal(0, saved.SizeBytes);
        Assert.False(storage.Objects.ContainsKey(rec.BlobKey)); // blob removed
    }

    [Fact]
    public async Task RunAsync_SkipsProtectedRecordings()
    {
        var rec = Rec(protectedAt: Now.AddDays(-5));
        var (db, storage) = Seed(rec);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(0, deleted);
        var saved = await db.Recordings.FindAsync(rec.Id);
        Assert.Null(saved!.AudioDeletedAt);
        Assert.Equal(1000, saved.SizeBytes);
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
    }

    [Fact]
    public async Task RunAsync_SkipsRecordingsInsideRetentionWindow()
    {
        var rec = Rec(createdAt: Recent);
        var (db, storage) = Seed(rec);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.AudioDeletedAt);
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
    }

    [Theory]
    [InlineData(RecordingStatus.Uploaded)]
    [InlineData(RecordingStatus.Queued)]
    [InlineData(RecordingStatus.Transcribing)]
    [InlineData(RecordingStatus.Failed)]
    [InlineData(RecordingStatus.Merging)]
    public async Task RunAsync_SkipsRecordingsWithoutACompletedTranscript(RecordingStatus status)
    {
        var rec = Rec(status: status);
        var (db, storage) = Seed(rec);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.AudioDeletedAt);
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
    }

    [Theory]
    [InlineData(RecordingStatus.Transcribed)]
    [InlineData(RecordingStatus.Summarized)]
    [InlineData(RecordingStatus.Summarizing)]
    public async Task RunAsync_DeletesAudio_ForEveryTranscribedStatus(RecordingStatus status)
    {
        var rec = Rec(status: status);
        var (db, storage) = Seed(rec);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(1, deleted);
        Assert.Equal(Now, (await db.Recordings.FindAsync(rec.Id))!.AudioDeletedAt);
    }

    [Fact]
    public async Task RunAsync_SkipsAudioAlreadyDeleted()
    {
        var rec = Rec(deletedAt: Now.AddDays(-2));
        rec.SizeBytes = 0;
        var (db, _) = Seed(rec);
        var storage = new FakeAudioStorage(); // no blob present

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.Equal(rec.AudioDeletedAt, (await db.Recordings.FindAsync(rec.Id))!.AudioDeletedAt); // unchanged
    }

    [Fact]
    public async Task RunAsync_MixedSet_DeletesOnlyEligible()
    {
        var eligible1 = Rec();
        var eligible2 = Rec(status: RecordingStatus.Summarized);
        var recent = Rec(createdAt: Recent);
        var prot = Rec(protectedAt: Now);
        var failed = Rec(status: RecordingStatus.Failed);
        var (db, storage) = Seed(eligible1, eligible2, recent, prot, failed);

        var deleted = await AudioRetentionSweep.RunAsync(db, storage, Now, RetentionDays, NullLogger.Instance);

        Assert.Equal(2, deleted);
        Assert.False(storage.Objects.ContainsKey(eligible1.BlobKey));
        Assert.False(storage.Objects.ContainsKey(eligible2.BlobKey));
        Assert.True(storage.Objects.ContainsKey(recent.BlobKey));
        Assert.True(storage.Objects.ContainsKey(prot.BlobKey));
        Assert.True(storage.Objects.ContainsKey(failed.BlobKey));
    }
}
