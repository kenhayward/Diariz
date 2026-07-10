using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class RoomsControllerTests
{
    private static RoomsController Build(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        return new(new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
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
}
