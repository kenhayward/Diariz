using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class UserGroupModelTests
{
    [Fact]
    public async Task Group_WithMembers_RoundTrips()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = "Administrators",
            Permissions = PlatformPermission.ManageUsers | PlatformPermission.ManageRooms,
        };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        var loaded = db.UserGroups.Single();
        Assert.Equal("Administrators", loaded.Name);
        Assert.True(loaded.Permissions.HasFlag(PlatformPermission.ManageUsers));
        Assert.False(loaded.Permissions.HasFlag(PlatformPermission.ManagePlatform));
        Assert.False(loaded.IsSystem);
        Assert.Equal(userId, db.UserGroupMembers.Single().UserId);
    }
}
