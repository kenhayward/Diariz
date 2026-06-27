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
