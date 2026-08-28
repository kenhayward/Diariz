using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class ActionsControllerTests
{
    private static ActionsController Build(DiarizDbContext db, Guid userId) =>
        new(db, new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };

    private static Recording AddRecording(DiarizDbContext db, Guid userId, string title, string? name = null)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", Title = title, Name = name };
        db.Recordings.Add(rec);
        return rec;
    }

    [Fact]
    public async Task List_ReturnsAllOwnedActions_WithRecordingNames_ExcludesOtherUsers()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec1 = AddRecording(db, me, "Standup", name: "Daily standup");
        var rec2 = AddRecording(db, me, "Planning");
        var theirs = AddRecording(db, Guid.NewGuid(), "Secret");
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec1.Id, Text = "A1", Actor = "Bob", Ordinal = 0 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec2.Id, Text = "A2", Actor = "Alice", Ordinal = 0 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = theirs.Id, Text = "Nope", Ordinal = 0 });
        await db.SaveChangesAsync();

        var result = await Build(db, me).List();

        var dtos = Assert.IsType<List<ActionListItemDto>>(result.Value);
        Assert.Equal(2, dtos.Count);
        Assert.DoesNotContain(dtos, d => d.Text == "Nope");
        // RecordingName = Name ?? Title.
        Assert.Contains(dtos, d => d.Text == "A1" && d.RecordingName == "Daily standup");
        Assert.Contains(dtos, d => d.Text == "A2" && d.RecordingName == "Planning");
        Assert.All(dtos, d => Assert.False(d.Completed));
    }

    [Fact]
    public async Task List_WithPinnedTrue_ReturnsOnlyPinnedActions()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        var dtos = (await Build(db, me).List(pinned: true)).Value!;

        Assert.Single(dtos);
        Assert.Equal("Book the room", dtos[0].Text);
        Assert.True(dtos[0].Pinned);
    }

    [Fact]
    public async Task List_WithNoPinnedParameter_StillReturnsEveryAction()
    {
        // The published default, deliberately unchanged. GET /api/actions is in the OpenAPI document and is
        // what the n8n community node calls as "List action items across meetings". Narrowing it to
        // pinned-only would break live workflows silently, and an npm-published node cannot be corrected
        // after the fact. This test is the guard on that decision.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        var dtos = (await Build(db, me).List()).Value!;

        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.Text == "Chase the invoice" && !d.Pinned);
    }

    [Fact]
    public async Task List_ByRoom_ReturnsActionsSharedIntoThatRoom_ExcludesPersonalOnes()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Users.Ensure(db, me);
        Users.Ensure(db, owner);
        var scope = new Diariz.Api.Services.RoomScope(db);

        var shared = AddRecording(db, owner, "Team sync"); // another user's recording, shared into the room
        var mine = AddRecording(db, me, "Personal");        // my own recording, NOT in the room
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = shared.Id, Text = "Shared action", Ordinal = 0 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = mine.Id, Text = "Personal action", Ordinal = 0 });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(shared.Id, owner, sectionId: null);
        await scope.PlaceInMainRoomAsync(mine.Id, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(shared.Id, roomId, owner, sectionId: null);

        var dtos = (await Build(db, me).List(roomId)).Value!;
        Assert.Contains(dtos, d => d.Text == "Shared action");
        Assert.DoesNotContain(dtos, d => d.Text == "Personal action");
    }

    [Fact]
    public async Task List_ByRoom_404_ForANonMember()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var roomId = await new Diariz.Api.Services.RoomScope(db).CreateSharedRoomAsync("Eng", null, null, null);
        var result = await Build(db, me).List(roomId); // never joined
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Complete_SetsCompletedAndTimestamp_OnlyForOwnedIds()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = AddRecording(db, me, "Mine");
        var theirs = AddRecording(db, Guid.NewGuid(), "Theirs");
        var a1 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = mine.Id, Text = "A1", Ordinal = 0 };
        var a2 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = theirs.Id, Text = "A2", Ordinal = 0 };
        db.RecordingActions.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var result = await Build(db, me).Complete(new CompleteActionsRequest(new[] { a1.Id, a2.Id }, true));

        Assert.IsType<NoContentResult>(result);
        var s1 = await db.RecordingActions.FindAsync(a1.Id);
        Assert.True(s1!.Completed);
        Assert.NotNull(s1.CompletedAt);
        var s2 = await db.RecordingActions.FindAsync(a2.Id); // other user's action untouched
        Assert.False(s2!.Completed);
        Assert.Null(s2.CompletedAt);
    }

    [Fact]
    public async Task Pin_SetsAndClearsTheFlag()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        var action = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0 };
        db.RecordingActions.Add(action);
        await db.SaveChangesAsync();

        await Build(db, me).Pin(new PinActionsRequest(new[] { action.Id }, true));
        Assert.True((await db.RecordingActions.FindAsync(action.Id))!.Pinned);

        await Build(db, me).Pin(new PinActionsRequest(new[] { action.Id }, false));
        Assert.False((await db.RecordingActions.FindAsync(action.Id))!.Pinned);
    }

    [Fact]
    public async Task Pin_IgnoresActionsBelongingToAnotherUser()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = AddRecording(db, me, "Standup");
        var theirs = AddRecording(db, Guid.NewGuid(), "Their meeting");
        var ours = new RecordingAction { Id = Guid.NewGuid(), RecordingId = mine.Id, Text = "Mine", Ordinal = 0 };
        var hers = new RecordingAction { Id = Guid.NewGuid(), RecordingId = theirs.Id, Text = "Not mine", Ordinal = 0 };
        db.RecordingActions.AddRange(ours, hers);
        await db.SaveChangesAsync();

        // A mixed selection still works: the owned id is pinned, the other silently skipped rather than 403.
        await Build(db, me).Pin(new PinActionsRequest(new[] { ours.Id, hers.Id }, true));

        Assert.True((await db.RecordingActions.FindAsync(ours.Id))!.Pinned);
        Assert.False((await db.RecordingActions.FindAsync(hers.Id))!.Pinned);
    }

    [Fact]
    public async Task Pin_WithEmptyIdList_SucceedsWithoutDoingAnything()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();

        var result = await Build(db, me).Pin(new PinActionsRequest(Array.Empty<Guid>(), true));

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Complete_False_ClearsCompletedAt()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "R");
        var a = new RecordingAction
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "A", Ordinal = 0,
            Completed = true, CompletedAt = DateTimeOffset.UtcNow,
        };
        db.RecordingActions.Add(a);
        await db.SaveChangesAsync();

        await Build(db, me).Complete(new CompleteActionsRequest(new[] { a.Id }, false));

        var s = await db.RecordingActions.FindAsync(a.Id);
        Assert.False(s!.Completed);
        Assert.Null(s.CompletedAt);
    }
}
