using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class StorageBackfillTests
{
    [Fact]
    public async Task RunAsync_SetsSizeFromStorage_ForLegacyRows_LeavingSizedRowsUntouched()
    {
        using var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        var userId = Guid.NewGuid();
        var r1 = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "u/a.webm", SizeBytes = 0 };
        var r2 = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "u/b.webm", SizeBytes = 0 };
        var alreadySized = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "u/c.webm", SizeBytes = 999 };
        db.Recordings.AddRange(r1, r2, alreadySized);
        await db.SaveChangesAsync();
        storage.Objects["u/a.webm"] = new byte[1200];
        storage.Objects["u/b.webm"] = new byte[800];
        storage.Objects["u/c.webm"] = new byte[5];

        var updated = await StorageBackfill.RunAsync(db, storage, NullLogger.Instance);

        Assert.Equal(2, updated);
        Assert.Equal(1200, (await db.Recordings.FindAsync(r1.Id))!.SizeBytes);
        Assert.Equal(800, (await db.Recordings.FindAsync(r2.Id))!.SizeBytes);
        Assert.Equal(999, (await db.Recordings.FindAsync(alreadySized.Id))!.SizeBytes); // not re-sized
    }

    [Fact]
    public async Task RunAsync_SkipsRowsWhoseBlobIsMissing()
    {
        using var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "gone", SizeBytes = 0 };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();

        var updated = await StorageBackfill.RunAsync(db, storage, NullLogger.Instance);

        Assert.Equal(0, updated);
        Assert.Equal(0, (await db.Recordings.FindAsync(rec.Id))!.SizeBytes);
    }

    [Fact]
    public async Task RunAsync_NoLegacyRows_ReturnsZero()
    {
        using var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "u/a.webm", SizeBytes = 500 };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();

        Assert.Equal(0, await StorageBackfill.RunAsync(db, new FakeAudioStorage(), NullLogger.Instance));
    }
}
