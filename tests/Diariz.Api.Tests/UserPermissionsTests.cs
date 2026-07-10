using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class UserPermissionsTests
{
    private static async Task AddGroupWithMember(DiarizDbContext db, Guid userId, PlatformPermission perms, string name)
    {
        var group = new UserGroup { Id = Guid.NewGuid(), Name = name, Permissions = perms };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoGroups_GrantsNothing()
    {
        using var db = TestDb.Create();
        var sut = new UserPermissions(db);
        Assert.Equal(PlatformPermission.None, await sut.ForAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MembershipOfTwoGroups_UnionsTheirFlags()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await AddGroupWithMember(db, userId, PlatformPermission.ManageUsers, "A");
        await AddGroupWithMember(db, userId, PlatformPermission.ManagePlatform, "B");

        var perms = await new UserPermissions(db).ForAsync(userId);

        Assert.True(perms.HasFlag(PlatformPermission.ManageUsers));
        Assert.True(perms.HasFlag(PlatformPermission.ManagePlatform));
        Assert.False(perms.HasFlag(PlatformPermission.ManageRooms));
    }

    [Fact]
    public async Task AnotherUsersGroup_GrantsNothing()
    {
        using var db = TestDb.Create();
        await AddGroupWithMember(db, Guid.NewGuid(), PlatformPermission.ManagePlatform, "A");
        Assert.Equal(PlatformPermission.None, await new UserPermissions(db).ForAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HasAsync_IsAnyOf_NotAllOf()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await AddGroupWithMember(db, userId, PlatformPermission.ManageUsers, "A");
        var sut = new UserPermissions(db);

        // "Any of" is what lets one policy express "manage users OR manage platform" (ReadAdminSettings).
        Assert.True(await sut.HasAsync(userId, PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform));
        Assert.False(await sut.HasAsync(userId, PlatformPermission.ManagePlatform));
    }
}
