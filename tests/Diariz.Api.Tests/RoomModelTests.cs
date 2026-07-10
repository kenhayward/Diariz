using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomModelTests
{
    [Fact]
    public async Task PersonalRoom_WithAnOwnerMember_RoundTrips()
    {
        using var db = TestDb.Create();
        var ownerId = Guid.NewGuid();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = "Ken Hayward",
            Kind = RoomKind.Personal,
            OwnerUserId = ownerId,
        };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id,
            PrincipalType = RoomPrincipalType.User,
            PrincipalId = ownerId,
            Permissions = RoomPermission.ManageRoom | RoomPermission.CreateRecording,
        });
        await db.SaveChangesAsync();

        var loaded = db.Rooms.Single();
        Assert.Equal(RoomKind.Personal, loaded.Kind);
        Assert.Equal(ownerId, loaded.OwnerUserId);

        var member = db.RoomMembers.Single();
        Assert.Equal(RoomPrincipalType.User, member.PrincipalType);
        Assert.True(member.Permissions.HasFlag(RoomPermission.CreateRecording));
        Assert.False(member.Permissions.HasFlag(RoomPermission.ShareOut));
    }

    [Fact]
    public void SharedRoom_HasNoOwner()
    {
        var room = new Room { Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared };
        Assert.Null(room.OwnerUserId);
    }
}
