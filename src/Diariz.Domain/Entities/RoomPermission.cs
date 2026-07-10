namespace Diariz.Domain.Entities;

/// <summary>What a member may do inside one room. Stored as an int and APPEND-ONLY: never renumber an existing
/// flag (Postgres holds the numeric value).
///
/// RemoveOthersRecordings is named for what it does. Because a recording's main room is always its recorder's
/// Personal Room, it can only ever unshare a recording from this room - never destroy it.</summary>
[Flags]
public enum RoomPermission
{
    None = 0,

    /// <summary>Change the room's settings and membership.</summary>
    ManageRoom = 1,

    /// <summary>Record or upload into this room, and receive recordings shared into it.</summary>
    CreateRecording = 2,

    /// <summary>Remove other people's recordings from this room (unshare, never destroy).</summary>
    RemoveOthersRecordings = 4,

    /// <summary>Share a recording from this room into another room.</summary>
    ShareOut = 8,

    /// <summary>Create, rename and delete folders, and move recordings between them.</summary>
    ManageContents = 16,

    /// <summary>Edit or regenerate other people's recordings (summary, minutes, actions, attachments).</summary>
    EditOthersRecordings = 32,
}
