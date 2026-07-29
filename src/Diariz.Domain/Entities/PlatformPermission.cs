namespace Diariz.Domain.Entities;

/// <summary>Platform-level authority, granted through <see cref="UserGroup"/> membership. Replaces the
/// Identity roles. Stored as an int and APPEND-ONLY: never renumber an existing flag (Postgres holds the
/// numeric value).</summary>
[Flags]
public enum PlatformPermission
{
    None = 0,

    /// <summary>Create, edit and delete rooms and their membership. NOT a grant to read a room's contents.</summary>
    ManageRooms = 1,

    /// <summary>Create, edit, enable and delete users and groups.</summary>
    ManageUsers = 2,

    /// <summary>Read and write platform settings, and run maintenance (backup / restore).</summary>
    ManagePlatform = 4,

    /// <summary>Create, edit and delete formulas.</summary>
    ManageFormulas = 8,

    /// <summary>Browse the people directory, and edit, delete or merge people other than yourself. The
    /// directory is platform-wide, so reading it exposes every external contact the organisation has ever
    /// recorded - searching by name to label a speaker stays open to everyone, but browsing the list does
    /// not. Does NOT gate a user's own biometric choices: opting out and erasing a voiceprint are always
    /// permitted on the person linked to your own account.</summary>
    ManagePeople = 16,
}
