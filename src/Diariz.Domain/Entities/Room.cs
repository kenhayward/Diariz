namespace Diariz.Domain.Entities;

/// <summary>A workspace: folders, recordings, voiceprints, chats and meeting types all live in one.
///
/// A Personal room belongs to exactly one user and renders their avatar rather than a stored icon. When that
/// user is deleted the room is ORPHANED (<see cref="OwnerUserId"/> becomes null), not cascaded: its recordings
/// survive in the shared rooms they were shared into. An orphaned room has no members and appears in no
/// switcher.</summary>
public class Room
{
    public Guid Id { get; set; }

    /// <summary>Unique among SHARED rooms only. Personal room names are display labels (the owner's name), and
    /// two users may legitimately share a name.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Icon key from the shared set. Null for personal rooms (the owner's avatar is shown instead).</summary>
    public string? Icon { get; set; }

    /// <summary>Hex background colour. Null for personal rooms.</summary>
    public string? Color { get; set; }

    public RoomKind Kind { get; set; }

    /// <summary>Set only for a personal room. Null on a shared room, and on an ORPHANED personal room whose
    /// owner was deleted.</summary>
    public Guid? OwnerUserId { get; set; }
    public ApplicationUser? Owner { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<RoomMember> Members { get; set; } = [];
}
