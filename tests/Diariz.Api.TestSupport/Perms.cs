using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Grants platform permissions to a test user. Authority comes from group membership read out of the
/// database, so a role claim on the ClaimsPrincipal proves nothing - tests must seed a real group.</summary>
public static class Perms
{
    /// <summary>Put <paramref name="userId"/> in a group carrying <paramref name="permissions"/>. The group
    /// name is unique per call, so repeated grants in one database never collide.</summary>
    public static void Grant(DiarizDbContext db, Guid userId, PlatformPermission permissions)
    {
        if (permissions == PlatformPermission.None) return;

        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = $"test-group-{Guid.NewGuid():N}",
            Permissions = permissions,
        };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        db.SaveChanges();
    }

    /// <summary>The flags the old <c>Administrator</c> role conferred.</summary>
    public const PlatformPermission Administrator = PlatformPermission.ManageRooms | PlatformPermission.ManageUsers;

    /// <summary>The flags the old <c>PlatformAdministrator</c> role conferred.</summary>
    public const PlatformPermission PlatformAdministrator =
        PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform;
}
