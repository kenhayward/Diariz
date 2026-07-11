namespace Diariz.Domain.Entities;

/// <summary>A user's or a group's membership of a room, with the permissions it confers. One table with a
/// discriminator rather than two, because resolving a caller's effective permissions unions across both and a
/// single table keeps that a single query.
///
/// There is no FK on <see cref="PrincipalId"/>: it points at <c>AspNetUsers</c> or <c>UserGroups</c> depending
/// on <see cref="PrincipalType"/>. The database therefore cannot cascade, so deleting a user or a group
/// **sweeps** its rows explicitly - <c>AdminUsersController.Delete</c> and <c>GroupsController.Delete</c> each
/// remove the matching rows before removing the principal. A surviving row would be inert on
/// a personal room today (a personal room resolves permissions from <c>Room.OwnerUserId</c> alone), but a live
/// grant in a shared room once those have members.</summary>
public class RoomMember
{
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public RoomPrincipalType PrincipalType { get; set; }

    /// <summary>An <c>AspNetUsers.Id</c> when <see cref="PrincipalType"/> is User, a <c>UserGroups.Id</c> when Group.</summary>
    public Guid PrincipalId { get; set; }

    public RoomPermission Permissions { get; set; }
}
