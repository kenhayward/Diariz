namespace Diariz.Domain.Entities;

/// <summary>The placement of a recording in a room. A recording has exactly one MAIN placement - always in its
/// recorder's Personal Room - plus one row per room it has been shared into.
///
/// The folder is a property of the PLACEMENT, not of the recording: the same recording can sit in "Q3 Reviews"
/// in one room and be ungrouped in another. That is why <c>Recording.SectionId</c> no longer exists.
///
/// Because the main room is always personal, deleting a shared room can only ever unshare - never destroy.</summary>
public class RoomRecording
{
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>True on exactly one row per recording, and that row's room is the personal room of
    /// <c>Recording.UserId</c>. Derivable, but stored: it keeps the room-scoped query a plain join, and leaves
    /// room for a future "move between rooms".</summary>
    public bool IsMainRoom { get; set; }

    /// <summary>The folder WITHIN THIS ROOM. Null = ungrouped. <c>ON DELETE SET NULL</c>, so deleting a folder
    /// ungroups its recordings rather than removing them from the room.</summary>
    public Guid? SectionId { get; set; }
    public Section? Section { get; set; }

    /// <summary>Null on the main-room row: nobody shared a recording into its own home.</summary>
    public Guid? SharedByUserId { get; set; }
    public DateTimeOffset? SharedAt { get; set; }
}
