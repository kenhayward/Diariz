namespace Diariz.Domain.Entities;

/// <summary>A room member is a user or a group. Append-only (stored as an int in Postgres).</summary>
public enum RoomPrincipalType
{
    User = 0,
    Group = 1,
}
