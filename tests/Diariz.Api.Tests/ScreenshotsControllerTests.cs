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

    private static ScreenshotsController Build(DiarizDbContext db, Guid userId, IAudioStorage? storage = null) =>
        new(db, storage ?? new FakeAudioStorage(), new StorageUsage(db), Options.Create(new ScreenshotOptions()), new RoomScope(db))
        {
            ControllerContext = Http.Context(userId),
        };

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
            db, storage, new StorageUsage(db), Options.Create(new ScreenshotOptions()), new RoomScope(db))
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
            db, new FakeAudioStorage(), new StorageUsage(db), Options.Create(new ScreenshotOptions { MaxBytes = 32 }),
            new RoomScope(db))
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

    // ---- Room co-viewer read access (screenshots are woven into the transcript, so anyone who can read the
    // recording via room membership must see its captures too - but only the owner may add/delete). ----

    /// <summary>Shares the setup recording into a fresh room and adds <paramref name="viewerId"/> as a member
    /// (any permission - membership alone is the read gate), returning the created capture's id.</summary>
    private static async Task<Guid> ShareWithCoViewerAsync(DiarizDbContext db, Guid ownerId, Guid recordingId, Guid viewerId)
    {
        Users.Ensure(db, viewerId);
        var rooms = new RoomScope(db);
        var roomId = await rooms.CreateSharedRoomAsync("Engineering", null, null, null);
        await rooms.SetMemberAsync(roomId, RoomPrincipalType.User, viewerId, RoomPermission.CreateRecording);
        await rooms.ShareIntoRoomAsync(recordingId, roomId, ownerId, sectionId: null);
        return roomId;
    }

    [Fact]
    public async Task List_ForARoomCoViewer_Succeeds()
    {
        var (owner, db, _, ownerId, recordingId) = Setup();
        await owner.Create(recordingId, Png(), Jpg(), 1_000, 10, 10);
        var viewerId = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, ownerId, recordingId, viewerId);

        var list = await Build(db, viewerId).List(recordingId);

        Assert.Single(list.Value!);
    }

    [Fact]
    public async Task ContentAndThumb_ForARoomCoViewer_Succeed()
    {
        var (owner, db, storage, ownerId, recordingId) = Setup();
        var created = Assert.IsType<ScreenshotDto>(
            (await owner.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Value);
        var viewerId = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, ownerId, recordingId, viewerId);
        var viewer = Build(db, viewerId, storage); // same storage instance the owner uploaded into

        Assert.IsType<FileStreamResult>(await viewer.Content(recordingId, created.Id));
        Assert.IsType<FileStreamResult>(await viewer.Thumb(recordingId, created.Id));
    }

    [Fact]
    public async Task Create_ForARoomCoViewer_ReturnsNotFound()
    {
        var (owner, db, _, ownerId, recordingId) = Setup();
        var viewerId = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, ownerId, recordingId, viewerId);

        var result = await Build(db, viewerId).Create(recordingId, Png(), Jpg(), 0, 10, 10);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_ForARoomCoViewer_ReturnsNotFound()
    {
        var (owner, db, _, ownerId, recordingId) = Setup();
        var created = Assert.IsType<ScreenshotDto>(
            (await owner.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Value);
        var viewerId = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, ownerId, recordingId, viewerId);

        var result = await Build(db, viewerId).Delete(recordingId, created.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AllEndpoints_ReturnNotFound_ForAStrangerWithNoRoomInCommon()
    {
        var (owner, db, _, ownerId, recordingId) = Setup();
        var created = Assert.IsType<ScreenshotDto>(
            (await owner.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Value);
        // Share the recording into a room the stranger is NOT a member of, so CanReadRecordingAsync's placement
        // loop actually runs (and rejects on the membership check) rather than exiting on an empty placement
        // list - the recording would otherwise have no placement at all in this test.
        var coViewerId = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, ownerId, recordingId, coViewerId);
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);
        var strangerController = Build(db, stranger);

        Assert.IsType<NotFoundResult>((await strangerController.List(recordingId)).Result);
        Assert.IsType<NotFoundResult>(await strangerController.Content(recordingId, created.Id));
        Assert.IsType<NotFoundResult>(await strangerController.Thumb(recordingId, created.Id));
        Assert.IsType<NotFoundResult>((await strangerController.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Result);
        Assert.IsType<NotFoundResult>(await strangerController.Delete(recordingId, created.Id));
    }
}
