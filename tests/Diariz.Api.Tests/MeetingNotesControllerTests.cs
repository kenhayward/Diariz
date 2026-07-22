using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingNotesControllerTests
{
    private static MeetingNotesController Build(DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db)) { ControllerContext = Http.Context(userId) };

    private static async Task<(Guid userId, Guid recId)> Seed(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (userId, rec.Id);
    }

    [Fact]
    public async Task Create_AppendsLines_AssignsOrdinals_SkipsBlank_AndTrims()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);

        var result = await Build(db, user).Create(rec, new CreateMeetingNotesRequest(
            [new("  Comp expectations  ", 61_000), new("   ", null), new("IPO experience APAC", null)]));

        var list = Assert.IsAssignableFrom<IReadOnlyList<MeetingNoteDto>>(result.Value);
        Assert.Equal(2, list.Count); // the blank line was skipped
        Assert.Equal("Comp expectations", list[0].Text); // trimmed
        Assert.Equal(61_000, list[0].CapturedAtMs);
        Assert.Equal([0, 1], list.Select(n => n.Ordinal));
    }

    [Fact]
    public async Task Create_ContinuesOrdinals_AfterExistingLines()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("one")]));

        var second = await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("two")]));
        Assert.Equal(1, Assert.IsAssignableFrom<IReadOnlyList<MeetingNoteDto>>(second.Value)[0].Ordinal);
    }

    [Fact]
    public async Task List_ReturnsOwnLines_InOrdinalOrder()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("a"), new("b")]));

        var list = (await Build(db, user).List(rec)).Value!;
        Assert.Equal(["a", "b"], list.Select(n => n.Text));
    }

    [Fact]
    public async Task Update_EditsTextOnly_TimestampImmutable()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        var created = (await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("x", 5_000)]))).Value![0];

        Assert.IsType<NoContentResult>(await Build(db, user).Update(rec, created.Id, new UpdateMeetingNoteRequest(" y ")));
        var row = await db.MeetingNotes.SingleAsync(n => n.Id == created.Id);
        Assert.Equal("y", row.Text);
        Assert.Equal(5_000, row.CapturedAtMs); // unchanged
    }

    [Fact]
    public async Task Delete_RemovesLine()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        var created = (await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Value![0];

        Assert.IsType<NoContentResult>(await Build(db, user).Delete(rec, created.Id));
        Assert.Empty(await db.MeetingNotes.ToListAsync());
    }

    [Fact]
    public async Task AllEndpoints_Return404_ForRecordingsTheCallerDoesNotOwn()
    {
        var db = TestDb.Create();
        var (_, rec) = await Seed(db);
        var stranger = Guid.NewGuid();

        Assert.IsType<NotFoundResult>((await Build(db, stranger).List(rec)).Result);
        Assert.IsType<NotFoundResult>((await Build(db, stranger).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Result);
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Update(rec, Guid.NewGuid(), new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Delete(rec, Guid.NewGuid()));
    }

    // ---- Room co-viewer read access (notes are woven into the transcript, so anyone who can read the
    // recording via room membership must see its lines too - but only the owner may add/edit/delete). ----

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
        var db = TestDb.Create();
        var (owner, rec) = await Seed(db);
        await Build(db, owner).Create(rec, new CreateMeetingNotesRequest([new("x")]));
        var viewer = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, owner, rec, viewer);

        var list = (await Build(db, viewer).List(rec)).Value!;

        Assert.Single(list);
    }

    [Fact]
    public async Task Create_ForARoomCoViewer_ReturnsNotFound()
    {
        var db = TestDb.Create();
        var (owner, rec) = await Seed(db);
        var viewer = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, owner, rec, viewer);

        var result = await Build(db, viewer).Create(rec, new CreateMeetingNotesRequest([new("x")]));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAndDelete_ForARoomCoViewer_ReturnNotFound()
    {
        var db = TestDb.Create();
        var (owner, rec) = await Seed(db);
        var created = (await Build(db, owner).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Value![0];
        var viewer = Guid.NewGuid();
        await ShareWithCoViewerAsync(db, owner, rec, viewer);

        Assert.IsType<NotFoundResult>(await Build(db, viewer).Update(rec, created.Id, new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NotFoundResult>(await Build(db, viewer).Delete(rec, created.Id));
    }

    [Fact]
    public async Task AllEndpoints_Return404_ForAStrangerWithNoRoomInCommon()
    {
        var db = TestDb.Create();
        var (owner, rec) = await Seed(db);
        var created = (await Build(db, owner).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Value![0];
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);

        Assert.IsType<NotFoundResult>((await Build(db, stranger).List(rec)).Result);
        Assert.IsType<NotFoundResult>((await Build(db, stranger).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Result);
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Update(rec, created.Id, new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Delete(rec, created.Id));
    }
}
