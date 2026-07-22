using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres check of the screenshot permission boundary: the owner can read and mutate; a room
/// co-viewer can read but is rejected on every mutating route; a stranger with no room in common is rejected
/// on everything. The room-membership query itself is exercised elsewhere (RoomRecordingsIntegrationTests,
/// TranscriptSearchRoomScopeTests); this closes the loop on the controller wiring against a real database.</summary>
[Collection(IntegrationCollection.Name)]
public class ScreenshotsIntegrationTests(ContainersFixture fx)
{
    private static IFormFile Png(int bytes = 8) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "full", "shot.png") { Headers = new HeaderDictionary() };

    private static IFormFile Jpg(int bytes = 4) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "thumb", "shot.jpg") { Headers = new HeaderDictionary() };

    private static ScreenshotsController Build(DiarizDbContext db, Guid userId, FakeAudioStorage storage) =>
        new(db, storage, new StorageUsage(db), Options.Create(new ScreenshotOptions()), new RoomScope(db))
        {
            ControllerContext = Http.Context(userId),
        };

    private async Task<(Guid ownerId, Guid recordingId)> SeedOwnerAndRecordingAsync()
    {
        await using var db = fx.CreateDbContext();
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "o@x.test", QuotaBytes = long.MaxValue };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = owner.Id, BlobKey = "k", Title = "R" };
        db.AddRange(owner, rec);
        await db.SaveChangesAsync();
        return (owner.Id, rec.Id);
    }

    private static async Task ShareWithCoViewerAsync(DiarizDbContext db, Guid ownerId, Guid recordingId, Guid viewerId)
    {
        db.Users.Add(new ApplicationUser { Id = viewerId, UserName = $"{viewerId}@x.test", Email = $"{viewerId}@x.test" });
        await db.SaveChangesAsync();
        var rooms = new RoomScope(db);
        var roomId = await rooms.CreateSharedRoomAsync($"Engineering {Guid.NewGuid():N}", null, null, null);
        await rooms.SetMemberAsync(roomId, RoomPrincipalType.User, viewerId, RoomPermission.CreateRecording);
        await rooms.ShareIntoRoomAsync(recordingId, roomId, ownerId, sectionId: null);
    }

    [Fact]
    public async Task Owner_CanReadAndMutate_OnRealPostgres()
    {
        var (ownerId, recId) = await SeedOwnerAndRecordingAsync();
        var storage = new FakeAudioStorage();

        await using (var db = fx.CreateDbContext())
        {
            var created = Assert.IsType<ScreenshotDto>(
                (await Build(db, ownerId, storage).Create(recId, Png(), Jpg(), 1_000, 10, 10)).Value);

            await using var verify = fx.CreateDbContext();
            var owner = Build(verify, ownerId, storage);
            Assert.Single((await owner.List(recId)).Value!);
            Assert.IsType<FileStreamResult>(await owner.Content(recId, created.Id));
            Assert.IsType<NoContentResult>(await owner.Delete(recId, created.Id));
        }
    }

    [Fact]
    public async Task CoViewer_CanRead_ButMutatingRoutesAreRejected_OnRealPostgres()
    {
        var (ownerId, recId) = await SeedOwnerAndRecordingAsync();
        var storage = new FakeAudioStorage();
        var viewerId = Guid.NewGuid();

        ScreenshotDto created;
        await using (var db = fx.CreateDbContext())
            created = Assert.IsType<ScreenshotDto>((await Build(db, ownerId, storage).Create(recId, Png(), Jpg(), 0, 10, 10)).Value);

        await using (var db = fx.CreateDbContext())
            await ShareWithCoViewerAsync(db, ownerId, recId, viewerId);

        await using var verify = fx.CreateDbContext();
        var viewer = Build(verify, viewerId, storage);

        Assert.Single((await viewer.List(recId)).Value!);
        Assert.IsType<FileStreamResult>(await viewer.Content(recId, created.Id));
        Assert.IsType<FileStreamResult>(await viewer.Thumb(recId, created.Id));

        Assert.IsType<NotFoundResult>((await viewer.Create(recId, Png(), Jpg(), 0, 10, 10)).Result);
        Assert.IsType<NotFoundResult>(await viewer.Delete(recId, created.Id));
    }

    [Fact]
    public async Task Stranger_IsRejectedOnEveryRoute_OnRealPostgres()
    {
        var (ownerId, recId) = await SeedOwnerAndRecordingAsync();
        var storage = new FakeAudioStorage();
        var strangerId = Guid.NewGuid();

        ScreenshotDto created;
        await using (var db = fx.CreateDbContext())
            created = Assert.IsType<ScreenshotDto>((await Build(db, ownerId, storage).Create(recId, Png(), Jpg(), 0, 10, 10)).Value);

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = strangerId, UserName = $"{strangerId}@x.test", Email = $"{strangerId}@x.test" });
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        var stranger = Build(verify, strangerId, storage);

        Assert.IsType<NotFoundResult>((await stranger.List(recId)).Result);
        Assert.IsType<NotFoundResult>(await stranger.Content(recId, created.Id));
        Assert.IsType<NotFoundResult>(await stranger.Thumb(recId, created.Id));
        Assert.IsType<NotFoundResult>((await stranger.Create(recId, Png(), Jpg(), 0, 10, 10)).Result);
        Assert.IsType<NotFoundResult>(await stranger.Delete(recId, created.Id));
    }
}
