using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RoomsControllerTests
{
    private static RoomsController Build(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        return new(new Diariz.Api.Services.RoomScope(db), db) { ControllerContext = Http.Context(userId) };
    }

    [Fact]
    public async Task List_ReturnsThePersonalRoom_WithFullPermissions()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value);
        var only = Assert.Single(rooms);
        Assert.True(only.IsPersonal);
        Assert.Equal(RoomKind.Personal, only.Kind);
        Assert.Equal((int)RoomPermission.CreateRecording, only.Permissions & (int)RoomPermission.CreateRecording);
        Assert.Equal((int)RoomPermission.ManageRoom, only.Permissions & (int)RoomPermission.ManageRoom);
    }

    [Fact]
    public async Task List_DoesNotReturnRoomsTheCallerIsNotAMemberOf()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = "Someone else", Kind = RoomKind.Shared });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value);
        Assert.Single(rooms); // only the personal room, minted on demand
    }

    [Fact]
    public async Task List_IncludesASharedRoomTheCallerIsAMemberOf()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var shared = new Room { Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared };
        db.Rooms.Add(shared);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = shared.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = me,
            Permissions = RoomPermission.CreateRecording,
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value).ToList();
        Assert.Equal(2, rooms.Count);
        Assert.True(rooms[0].IsPersonal); // personal first
        var eng = rooms.Single(r => !r.IsPersonal);
        Assert.Equal("Engineering", eng.Name);
        Assert.Equal((int)RoomPermission.CreateRecording, eng.Permissions);
    }

    // ---- Writes (Phase 4) ----

    [Fact]
    public async Task Create_MakesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());

        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(new RoomInput("Engineering", "the eng team", "users", "#123456")));
        var id = (Guid)created.RouteValues!["id"]!;

        var room = db.Rooms.Single(r => r.Id == id);
        Assert.Equal(RoomKind.Shared, room.Kind);
        Assert.Equal("Engineering", room.Name);
    }

    [Fact]
    public async Task Create_RejectsADuplicateSharedName()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        await controller.Create(new RoomInput("Engineering", null, null, null));

        Assert.IsType<ConflictObjectResult>(await controller.Create(new RoomInput("Engineering", null, null, null)));
    }

    [Fact]
    public async Task Update_RenamesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NoContentResult>(await controller.Update(id, new RoomInput("Engineering", "d", "star", "#abcdef")));
        Assert.Equal("Engineering", db.Rooms.Single(r => r.Id == id).Name);
    }

    [Fact]
    public async Task Update_RefusesThePersonalRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var personalId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(me);

        Assert.IsType<BadRequestObjectResult>(await controller.Update(personalId, new RoomInput("Hacked", null, null, null)));
    }

    [Fact]
    public async Task Delete_RemovesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NoContentResult>(await controller.Delete(id));
        Assert.False(db.Rooms.Any(r => r.Id == id));
    }

    [Fact]
    public async Task Delete_RefusesThePersonalRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var personalId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(me);

        Assert.IsType<BadRequestObjectResult>(await controller.Delete(personalId));
        Assert.True(db.Rooms.Any(r => r.Id == personalId));
    }

    [Fact]
    public async Task SetMember_ThenGet_ReturnsTheMembership()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;
        var member = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.SetMember(id,
            new RoomMemberInput(RoomPrincipalType.User, member, (int)RoomPermission.CreateRecording)));

        // Add the caller as a member so they can read the detail.
        await controller.SetMember(id, new RoomMemberInput(RoomPrincipalType.User, me, (int)RoomPermission.ManageRoom));
        var detail = Assert.IsType<RoomDetailDto>(Assert.IsType<OkObjectResult>(await controller.Get(id)).Value);
        Assert.Contains(detail.Members, m => m.PrincipalId == member && m.Permissions == (int)RoomPermission.CreateRecording);
    }

    [Fact]
    public async Task Get_HidesARoomTheCallerIsNotAMemberOf()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NotFoundResult>(await controller.Get(id)); // caller is not a member
    }

    // ---- Unshare (Phase 5) ----

    [Fact]
    public async Task RemoveRecording_Unshares_ForARoomMemberWhoRecordedIt()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = me, Title = "Standup" });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(recId, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(recId, roomId, me, sectionId: null);

        Assert.IsType<NoContentResult>(await controller.RemoveRecording(roomId, recId));
        Assert.False(await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId && p.RecordingId == recId));
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == recId)); // recording survives
    }

    [Fact]
    public async Task RemoveRecording_RefusesTheMainRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = me, Title = "Standup" });
        await db.SaveChangesAsync();
        var personalRoomId = await scope.PersonalRoomIdAsync(me);
        await scope.PlaceInMainRoomAsync(recId, me, sectionId: null);

        // The home room can't unshare - that would be a delete, from the recording detail.
        Assert.IsType<BadRequestObjectResult>(await controller.RemoveRecording(personalRoomId, recId));
        Assert.True(await db.RoomRecordings.AnyAsync(p => p.RoomId == personalRoomId && p.RecordingId == recId));
    }

    [Fact]
    public async Task RemoveRecording_403_ForANonRecorderWithoutRemoveOthers()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        Users.Ensure(db, owner);
        var controller = Build(db, other); // a member, but not the recorder, without RemoveOthersRecordings
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = owner, Title = "Standup" });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(recId, owner, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.ShareIntoRoomAsync(recId, roomId, owner, sectionId: null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, other, RoomPermission.CreateRecording); // no RemoveOthers

        Assert.Equal(403, ((ObjectResult)await controller.RemoveRecording(roomId, recId)).StatusCode);
        Assert.True(await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId && p.RecordingId == recId));
    }
}
