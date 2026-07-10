namespace Diariz.Domain.Entities;

/// <summary>Append-only (stored as an int in Postgres).</summary>
public enum RoomKind
{
    /// <summary>Exactly one per user, auto-created. Immutable and private: it cannot be renamed, deleted,
    /// shared, or gain members. A recording's main room is always its recorder's Personal Room.</summary>
    Personal = 0,

    Shared = 1,
}
