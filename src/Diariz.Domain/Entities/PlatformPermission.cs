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
}
