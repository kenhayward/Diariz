using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class StorageControllerTests
{
    private static StorageController Build(DiarizDbContext db, Guid userId) =>
        new(new StorageUsage(db), db) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Get_ReturnsSummedAudioBytes_AndQuota()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "u@x.test", Email = "u@x.test", QuotaBytes = 5000 });
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "a", SizeBytes = 1200 });
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "b", SizeBytes = 800 });
        // Another user's recording must not count.
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "c", SizeBytes = 9999 });
        await db.SaveChangesAsync();

        var dto = await Build(db, userId).Get();

        Assert.Equal(2000, dto.UsedBytes);
        Assert.Equal(5000, dto.QuotaBytes);
    }

    [Fact]
    public async Task Get_SumsTranscriptionProcessingTime_AcrossAllVersions()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "u@x.test", Email = "u@x.test", QuotaBytes = 5000 });
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "a", SizeBytes = 0 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1, ProcessingMs = 30000 });
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 2, ProcessingMs = 12000 });
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 3, ProcessingMs = null }); // ignored
        // Another user's transcription must not count.
        var other = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "b", SizeBytes = 0 };
        db.Recordings.Add(other);
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = other.Id, Model = "m", Version = 1, ProcessingMs = 99000 });
        await db.SaveChangesAsync();

        var dto = await Build(db, userId).Get();

        Assert.Equal(42000, dto.TotalTranscriptionMs);
    }

    [Fact]
    public async Task Get_NoRecordings_ReturnsZeroUsed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "u@x.test", Email = "u@x.test", QuotaBytes = 5000 });
        await db.SaveChangesAsync();

        var dto = await Build(db, userId).Get();

        Assert.Equal(0, dto.UsedBytes);
        Assert.Equal(5000, dto.QuotaBytes);
    }
}
