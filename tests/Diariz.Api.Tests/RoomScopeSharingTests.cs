using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Phase 5: the cross-room-sharing helpers on RoomScope - a recording's rooms, single-placement
/// unshare (never the main room), and the room ids a caller belongs to.</summary>
public class RoomScopeSharingTests
{
    private static async Task<(Guid userId, Guid recId, Guid personalRoomId)> SeedRecording(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var scope = new RoomScope(db);
        var personalRoomId = await scope.PersonalRoomIdAsync(userId);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = userId, Title = "Standup" });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(recId, userId, sectionId: null);
        return (userId, recId, personalRoomId);
    }

    [Fact]
    public async Task RoomsForRecording_ListsMainRoomFirst_ThenShared()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var (userId, recId, personalRoomId) = await SeedRecording(db);
        var sharedId = await scope.CreateSharedRoomAsync("Engineering", null, null, null);
        await scope.ShareIntoRoomAsync(recId, sharedId, userId, sectionId: null);

        var rooms = await scope.RoomsForRecordingAsync(recId);

        Assert.Equal(2, rooms.Count);
        Assert.True(rooms[0].IsMainRoom);
        Assert.Equal(personalRoomId, rooms[0].RoomId);
        Assert.Contains(rooms, r => !r.IsMainRoom && r.RoomId == sharedId && r.Name == "Engineering");
    }

    [Fact]
    public async Task RemoveFromRoom_DropsASharedPlacement_ButKeepsTheRecording()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var (userId, recId, _) = await SeedRecording(db);
        var sharedId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.ShareIntoRoomAsync(recId, sharedId, userId, sectionId: null);

        Assert.True(await scope.RemoveFromRoomAsync(recId, sharedId));

        Assert.False(await db.RoomRecordings.AnyAsync(p => p.RoomId == sharedId && p.RecordingId == recId));
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == recId)); // recording survives - only unshared
    }

    [Fact]
    public async Task RemoveFromRoom_RefusesTheMainRoom()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var (_, recId, personalRoomId) = await SeedRecording(db);

        Assert.False(await scope.RemoveFromRoomAsync(recId, personalRoomId));
        Assert.True(await db.RoomRecordings.AnyAsync(p => p.RoomId == personalRoomId && p.RecordingId == recId));
    }

    [Fact]
    public async Task RoomIdsForUser_IncludesPersonalAndSharedRooms()
    {
        using var db = TestDb.Create();
        var scope = new RoomScope(db);
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var personalRoomId = await scope.PersonalRoomIdAsync(userId);
        var sharedId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(sharedId, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);

        var ids = await scope.RoomIdsForUserAsync(userId);

        Assert.Contains(personalRoomId, ids);
        Assert.Contains(sharedId, ids);
    }
}
