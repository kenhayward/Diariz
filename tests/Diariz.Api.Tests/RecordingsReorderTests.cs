using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class RecordingsReorderTests
{
    private static RecordingsController Build(DiarizDbContext db, Guid userId)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions()), new FakeApiKeyProtector());
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier(), Options.Create(new UploadOptions()), new RoomScope(db))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    // Seeds a recording and its main placement (the folder now lives on the placement, not the recording).
    // Ensures the owner exists first, since PlaceInMainRoomAsync needs it to mint the personal room.
    private static async Task<Recording> Seed(DiarizDbContext db, Guid userId, Guid? sectionId = null)
    {
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId);
        return rec;
    }

    // Seeds a folder in its owner's personal room (folders are room-scoped now).
    private static async Task<Section> SeedSection(DiarizDbContext db, Guid owner, string name)
    {
        Users.Ensure(db, owner);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(owner);
        var s = new Section { Id = Guid.NewGuid(), UserId = owner, RoomId = roomId, Name = name };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // The folder a recording sits in, read from its placement in the owner's personal room.
    private static async Task<Guid?> FolderOf(DiarizDbContext db, Guid userId, Guid recordingId)
    {
        var scope = new RoomScope(db);
        return await scope.SectionIdAsync(await scope.PersonalRoomIdAsync(userId), recordingId);
    }

    // The per-room sort position of a recording, read from its placement in the given room.
    private static async Task<int> PositionOf(DiarizDbContext db, Guid roomId, Guid recordingId) =>
        (await db.RoomRecordings.SingleAsync(p => p.RoomId == roomId && p.RecordingId == recordingId)).Position;

    private static Task<int> PositionInPersonal(DiarizDbContext db, Guid userId, Guid recordingId) =>
        PositionOf(db, new RoomScope(db).PersonalRoomIdAsync(userId).Result, recordingId);

    [Fact]
    public async Task Reorder_SetsPositionsInOrder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var a = await Seed(db, userId);
        var b = await Seed(db, userId);
        var c = await Seed(db, userId);

        // New order: c, a, b
        var res = await Build(db, userId).Reorder(new ReorderRecordingsRequest(null, [c.Id, a.Id, b.Id]));

        Assert.IsType<NoContentResult>(res);
        // Order lives on the placement now, not the recording.
        Assert.Equal(0, await PositionInPersonal(db, userId, c.Id));
        Assert.Equal(1, await PositionInPersonal(db, userId, a.Id));
        Assert.Equal(2, await PositionInPersonal(db, userId, b.Id));
        Assert.Equal(0, (await db.Recordings.FindAsync(c.Id))!.Position); // global Position untouched (dead)
    }

    [Fact]
    public async Task Reorder_MovesRecordingsIntoTargetSection()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId, "Work");
        var a = await Seed(db, userId);
        var b = await Seed(db, userId);

        await Build(db, userId).Reorder(new ReorderRecordingsRequest(section.Id, [a.Id, b.Id]));

        Assert.Equal(section.Id, await FolderOf(db, userId, a.Id));
        Assert.Equal(section.Id, await FolderOf(db, userId, b.Id));
    }

    [Fact]
    public async Task Reorder_ToUngrouped_ClearsSection()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId, "Work");
        var a = await Seed(db, userId, section.Id);

        await Build(db, userId).Reorder(new ReorderRecordingsRequest(null, [a.Id]));

        Assert.Null(await FolderOf(db, userId, a.Id));
    }

    [Fact]
    public async Task Reorder_ForeignRecording_Returns404_AndChangesNothing()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = await Seed(db, me);
        var theirs = await Seed(db, Guid.NewGuid());

        // theirs isn't placed in my room, so the payload doesn't fully resolve here → 404, nothing changed.
        var res = await Build(db, me).Reorder(new ReorderRecordingsRequest(null, [mine.Id, theirs.Id]));

        Assert.IsType<NotFoundResult>(res);
        Assert.Equal(0, await PositionInPersonal(db, me, mine.Id)); // untouched
    }

    [Fact]
    public async Task Reorder_ForeignSection_Returns404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = await Seed(db, me);
        var foreignSection = await SeedSection(db, Guid.NewGuid(), "Theirs");

        var res = await Build(db, me).Reorder(new ReorderRecordingsRequest(foreignSection.Id, [mine.Id]));

        Assert.IsType<NotFoundResult>(res);
    }

    [Fact]
    public async Task List_OrdersByPosition()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var a = await Seed(db, userId);
        var b = await Seed(db, userId);
        await Build(db, userId).Reorder(new ReorderRecordingsRequest(null, [b.Id, a.Id])); // b first

        var list = (await Build(db, userId).List()).Value!;

        Assert.Equal([b.Id, a.Id], list.Select(r => r.Id));
    }

    // ---- Shared-room reorder (per-placement order + permission gate) ----

    // A shared room the member holds `perm` in, with `recIds` shared into it (owned by their recorder).
    private static async Task<Guid> SharedRoomWith(DiarizDbContext db, Guid member, RoomPermission perm, params Guid[] recIds)
    {
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, member, perm);
        foreach (var id in recIds)
            await scope.ShareIntoRoomAsync(id, roomId, member, sectionId: null);
        return roomId;
    }

    [Fact]
    public async Task Reorder_InSharedRoom_OrdersThePlacementsThere_IndependentlyOfPersonal()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var a = await Seed(db, me);
        var b = await Seed(db, me);
        var roomId = await SharedRoomWith(db, me, RoomPermission.ManageContents | RoomPermission.CreateRecording, a.Id, b.Id);

        // Personal order: a, b. Shared-room order: b, a.
        await Build(db, me).Reorder(new ReorderRecordingsRequest(null, [a.Id, b.Id]));
        await Build(db, me).Reorder(new ReorderRecordingsRequest(null, [b.Id, a.Id], RoomId: roomId));

        Assert.Equal(0, await PositionInPersonal(db, me, a.Id)); // personal unchanged
        Assert.Equal(1, await PositionInPersonal(db, me, b.Id));
        Assert.Equal(0, await PositionOf(db, roomId, b.Id));     // room has its own order
        Assert.Equal(1, await PositionOf(db, roomId, a.Id));
    }

    [Fact]
    public async Task Reorder_InSharedRoom_WithoutManageContents_Is403()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var a = await Seed(db, me);
        var roomId = await SharedRoomWith(db, me, RoomPermission.CreateRecording, a.Id); // no ManageContents

        var res = await Build(db, me).Reorder(new ReorderRecordingsRequest(null, [a.Id], RoomId: roomId));
        Assert.IsType<ForbidResult>(res);
    }

    [Fact]
    public async Task Reorder_InRoom_TheCallerIsNotAMemberOf_Is404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var roomId = await new RoomScope(db).CreateSharedRoomAsync("Eng", null, null, null); // never joined

        var res = await Build(db, me).Reorder(new ReorderRecordingsRequest(null, [Guid.NewGuid()], RoomId: roomId));
        Assert.IsType<NotFoundResult>(res);
    }
}
