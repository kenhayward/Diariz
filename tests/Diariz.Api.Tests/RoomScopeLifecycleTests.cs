using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Phase 4: RoomScope grows room lifecycle + membership. Personal rooms are immutable and memberless;
/// deleting a shared room unshares (never destroys) recordings.</summary>
public class RoomScopeLifecycleTests
{
    private static async Task<Guid> SeedUser(DiarizDbContext db)
    {
        var id = Guid.NewGuid();
        Users.Ensure(db, id);
        await Task.CompletedTask;
        return id;
    }

    [Fact]
    public async Task CreateSharedRoom_MakesAKindSharedRoom()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);

        var id = await scope.CreateSharedRoomAsync("Engineering", "the eng team", "users", "#123456");

        var room = await db.Rooms.SingleAsync(r => r.Id == id);
        Assert.Equal(RoomKind.Shared, room.Kind);
        Assert.Equal("Engineering", room.Name);
        Assert.Null(room.OwnerUserId);
    }

    [Fact]
    public async Task UpdateRoom_RenamesAndRestyles()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var id = await scope.CreateSharedRoomAsync("Eng", null, null, null);

        Assert.True(await scope.UpdateRoomAsync(id, "Engineering", "desc", "star", "#abcdef"));

        var room = await db.Rooms.SingleAsync(r => r.Id == id);
        Assert.Equal("Engineering", room.Name);
        Assert.Equal("desc", room.Description);
        Assert.Equal("star", room.Icon);
        Assert.Equal("#abcdef", room.Color);
    }

    [Fact]
    public async Task UpdateRoom_RefusesAPersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = await SeedUser(db);
        var scope = new RoomScope(db);
        var personalId = await scope.PersonalRoomIdAsync(userId);

        Assert.False(await scope.UpdateRoomAsync(personalId, "Hacked", null, null, null));
        Assert.NotEqual("Hacked", (await db.Rooms.SingleAsync(r => r.Id == personalId)).Name);
    }

    [Fact]
    public async Task DeleteRoom_RefusesAPersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = await SeedUser(db);
        var scope = new RoomScope(db);
        var personalId = await scope.PersonalRoomIdAsync(userId);

        Assert.False(await scope.DeleteRoomAsync(personalId));
        Assert.True(await db.Rooms.AnyAsync(r => r.Id == personalId));
    }

    [Fact]
    public async Task DeleteRoom_UnsharesButDoesNotDestroyTheRecording()
    {
        using var db = TestDb.Create();
        var userId = await SeedUser(db);
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = userId, Title = "Standup" });
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId, RecordingId = recId, IsMainRoom = false, SharedByUserId = userId, SharedAt = default,
        });
        await db.SaveChangesAsync();

        Assert.True(await scope.DeleteRoomAsync(roomId));

        Assert.False(await db.Rooms.AnyAsync(r => r.Id == roomId));
        Assert.False(await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId));
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == recId)); // the recording survives - only unshared
    }

    [Fact]
    public async Task SetMember_UpsertsThePermissionGrid()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        var principal = Guid.NewGuid();

        Assert.True(await scope.SetMemberAsync(roomId, RoomPrincipalType.User, principal, RoomPermission.CreateRecording));
        Assert.True(await scope.SetMemberAsync(roomId, RoomPrincipalType.User, principal,
            RoomPermission.CreateRecording | RoomPermission.ManageContents));

        var row = await db.RoomMembers.SingleAsync(m => m.RoomId == roomId && m.PrincipalId == principal);
        Assert.Equal(RoomPermission.CreateRecording | RoomPermission.ManageContents, row.Permissions);
    }

    [Fact]
    public async Task RemoveMember_DropsTheRow()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        var principal = Guid.NewGuid();
        await scope.SetMemberAsync(roomId, RoomPrincipalType.Group, principal, RoomPermission.CreateRecording);

        Assert.True(await scope.RemoveMemberAsync(roomId, RoomPrincipalType.Group, principal));
        Assert.False(await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.PrincipalId == principal));
    }

    [Fact]
    public async Task SetMember_RefusesAPersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = await SeedUser(db);
        var scope = new RoomScope(db);
        var personalId = await scope.PersonalRoomIdAsync(userId);

        Assert.False(await scope.SetMemberAsync(personalId, RoomPrincipalType.User, Guid.NewGuid(), RoomPermission.ManageRoom));
    }
}
