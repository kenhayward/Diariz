namespace Diariz.Domain.Entities;

/// <summary>A user's or a group's membership of a room, with the permissions it confers. One table with a
/// discriminator rather than two, because resolving a caller's effective permissions unions across both and a
/// single table keeps that a single query.
///
/// There is no FK on <see cref="PrincipalId"/>: it points at <c>AspNetUsers</c> or <c>UserGroups</c> depending
/// on <see cref="PrincipalType"/>. Rows are cleaned up by the user/group delete paths, not by the database.</summary>
public class RoomMember
{
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public RoomPrincipalType PrincipalType { get; set; }

    /// <summary>An <c>AspNetUsers.Id</c> when <see cref="PrincipalType"/> is User, a <c>UserGroups.Id</c> when Group.</summary>
    public Guid PrincipalId { get; set; }

    public RoomPermission Permissions { get; set; }
}
