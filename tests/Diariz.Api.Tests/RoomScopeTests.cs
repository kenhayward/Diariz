using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomScopeTests
{
    private static readonly RoomPermission All =
        RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
        RoomPermission.ShareOut | RoomPermission.ManageContents | RoomPermission.EditOthersRecordings;

    private static async Task<Guid> NewUserAsync(DiarizDbContext db, string? fullName = null)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test",
            FullName = fullName,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Room> SharedRoomAsync(DiarizDbContext db, string name = "Engineering")
    {
        var room = new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private static async Task MemberAsync(
        DiarizDbContext db, Guid roomId, RoomPrincipalType type, Guid principalId, RoomPermission perms)
    {
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = roomId, PrincipalType = type, PrincipalId = principalId, Permissions = perms,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PersonalRoom_IsCreatedOnFirstAsk_AndNamedAfterTheUser()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada Lovelace");
        var sut = new RoomScope(db);

        var roomId = await sut.PersonalRoomIdAsync(userId);

        var room = db.Rooms.Single();
        Assert.Equal(roomId, room.Id);
        Assert.Equal("Ada Lovelace", room.Name);
        Assert.Equal(RoomKind.Personal, room.Kind);
        Assert.Equal(userId, room.OwnerUserId);
    }

    [Fact]
    public async Task PersonalRoom_IsCreatedOnce()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var sut = new RoomScope(db);

        var first = await sut.PersonalRoomIdAsync(userId);
        var second = await sut.PersonalRoomIdAsync(userId);

        Assert.Equal(first, second);
        Assert.Single(db.Rooms);
        Assert.Single(db.RoomMembers);
    }

    [Fact]
    public async Task PersonalRoom_FallsBackToEmail_WhenTheUserHasNoDisplayName()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);

        await sut.PersonalRoomIdAsync(userId);

        Assert.False(string.IsNullOrWhiteSpace(db.Rooms.Single().Name));
    }

    [Fact]
    public async Task PersonalRoomOwner_HoldsEveryPermission()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);

        Assert.Equal(All, await sut.PermissionsAsync(userId, roomId));
    }

    [Fact]
    public async Task SharedRoom_UnionsTheUsersOwnRowWithTheirGroupsRows()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);

        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);
        await MemberAsync(db, room.Id, RoomPrincipalType.Group, group.Id, RoomPermission.ShareOut);

        var perms = await new RoomScope(db).PermissionsAsync(userId, room.Id);

        Assert.True(perms.HasFlag(RoomPermission.CreateRecording));
        Assert.True(perms.HasFlag(RoomPermission.ShareOut));
        Assert.False(perms.HasFlag(RoomPermission.ManageRoom));
    }

    [Fact]
    public async Task NonMember_HoldsNothing_AndIsNotAMember()
    {
        using var db = TestDb.Create();
        var stranger = await NewUserAsync(db, "Eve");
        var room = await SharedRoomAsync(db);
        var sut = new RoomScope(db);

        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(stranger, room.Id));
        Assert.False(await sut.IsMemberAsync(stranger, room.Id));
    }

    /// <summary>Membership is row existence, not "holds some permission". A member granted nothing can still
    /// see the room; if IsMemberAsync inferred membership from the flags, they would be 404'd out of it.</summary>
    [Fact]
    public async Task MemberWithNoPermissions_IsStillAMember()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);
        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.None);
        var sut = new RoomScope(db);

        Assert.True(await sut.IsMemberAsync(userId, room.Id));
        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(userId, room.Id));
    }

    /// <summary>Another user's personal room grants a stranger nothing: the owner override is keyed on OwnerUserId.</summary>
    [Fact]
    public async Task AnotherUsersPersonalRoom_GrantsAStrangerNothing()
    {
        using var db = TestDb.Create();
        var owner = await NewUserAsync(db, "Ada");
        var stranger = await NewUserAsync(db, "Eve");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(owner);

        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(stranger, roomId));
    }

    [Fact]
    public async Task StrangerIsNotAMemberOfAnotherUsersPersonalRoom()
    {
        using var db = TestDb.Create();
        var owner = await NewUserAsync(db, "Ada");
        var stranger = await NewUserAsync(db, "Eve");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(owner);

        Assert.True(await sut.IsMemberAsync(owner, roomId));
        Assert.False(await sut.IsMemberAsync(stranger, roomId));
    }

    [Fact]
    public async Task Require_ThrowsWhenThePermissionIsMissing_AndReturnsWhenHeld()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);
        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);
        var sut = new RoomScope(db);

        await sut.RequireAsync(userId, room.Id, RoomPermission.CreateRecording); // must not throw

        var ex = await Assert.ThrowsAsync<RoomForbiddenException>(
            () => sut.RequireAsync(userId, room.Id, RoomPermission.ManageRoom));
        Assert.Contains("ManageRoom", ex.Message);
    }
}
