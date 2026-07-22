using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ScreenshotsControllerTests
{
    private static IFormFile Png(int bytes = 64) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "full", "shot.png") { Headers = new HeaderDictionary() };

    private static IFormFile Jpg(int bytes = 16) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "thumb", "shot.jpg") { Headers = new HeaderDictionary() };

    private static (ScreenshotsController Controller, DiarizDbContext Db, FakeAudioStorage Storage, Guid UserId, Guid RecordingId)
        Setup(long quotaBytes = 1024 * 1024)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "a@b.c", UserName = "a@b.c", QuotaBytes = quotaBytes });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.SaveChanges();

        var storage = new FakeAudioStorage();
        var controller = new ScreenshotsController(
            db, storage, new StorageUsage(db), Options.Create(new ScreenshotOptions()))
        {
            ControllerContext = Http.Context(userId),
        };
        return (controller, db, storage, userId, recordingId);
    }

    [Fact]
    public async Task Create_StoresBothBlobsAndTheCaptureFacts()
    {
        var (controller, db, storage, userId, recordingId) = Setup();

        var result = await controller.Create(recordingId, Png(), Jpg(), capturedAtMs: 12_500, width: 1920, height: 1080);

        var dto = Assert.IsType<ScreenshotDto>(result.Value);
        Assert.Equal(12_500, dto.CapturedAtMs);
        Assert.Equal(80, dto.SizeBytes); // 64 + 16

        var row = await db.MeetingScreenshots.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Equal($"{userId}/screenshots/{row.Id}.png", row.BlobKey);
        Assert.Equal($"{userId}/screenshots/{row.Id}.thumb.jpg", row.ThumbBlobKey);
        Assert.Contains(row.BlobKey, storage.Objects.Keys);
        Assert.Contains(row.ThumbBlobKey, storage.Objects.Keys);
    }

    [Fact]
    public async Task Create_AssignsIncreasingOrdinals()
    {
        var (controller, _, _, _, recordingId) = Setup();

        await controller.Create(recordingId, Png(), Jpg(), 1_000, 100, 100);
        var second = await controller.Create(recordingId, Png(), Jpg(), 2_000, 100, 100);

        Assert.Equal(1, Assert.IsType<ScreenshotDto>(second.Value).Ordinal);
    }

    [Fact]
    public async Task Create_ForAnotherUsersRecording_ReturnsNotFound()
    {
        var (controller, db, _, _, _) = Setup();
        var otherId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = otherId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        var result = await controller.Create(otherId, Png(), Jpg(), 0, 10, 10);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_OverQuota_IsRejectedAndStoresNothing()
    {
        var (controller, db, storage, _, recordingId) = Setup(quotaBytes: 50);

        var result = await controller.Create(recordingId, Png(64), Jpg(16), 0, 10, 10);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
        Assert.Empty(storage.Objects);
        Assert.Empty(db.MeetingScreenshots);
    }

    [Fact]
    public async Task Create_OverTheSizeCap_IsRejected()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "a@b.c", UserName = "a@b.c", QuotaBytes = long.MaxValue });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.SaveChanges();
        var controller = new ScreenshotsController(
            db, new FakeAudioStorage(), new StorageUsage(db), Options.Create(new ScreenshotOptions { MaxBytes = 32 }))
        {
            ControllerContext = Http.Context(userId),
        };

        var result = await controller.Create(recordingId, Png(64), Jpg(16), 0, 10, 10);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsTheRecordingsCapturesInCaptureOrder()
    {
        var (controller, _, _, _, recordingId) = Setup();
        await controller.Create(recordingId, Png(), Jpg(), 9_000, 10, 10);
        await controller.Create(recordingId, Png(), Jpg(), 3_000, 10, 10);

        var list = await controller.List(recordingId);

        Assert.Equal(new long[] { 3_000, 9_000 }, list.Value!.Select(s => s.CapturedAtMs).ToArray());
    }

    [Fact]
    public async Task Delete_RemovesTheRowAndBothBlobs()
    {
        var (controller, db, storage, _, recordingId) = Setup();
        var created = Assert.IsType<ScreenshotDto>((await controller.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Value);

        var result = await controller.Delete(recordingId, created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.MeetingScreenshots);
        Assert.Empty(storage.Objects);
    }
}
