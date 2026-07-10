namespace Diariz.Domain.Entities;

/// <summary>Join row: a user's membership of a <see cref="UserGroup"/>. Composite key (GroupId, UserId).</summary>
public class UserGroupMember
{
    public Guid GroupId { get; set; }
    public UserGroup? Group { get; set; }

    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
}
