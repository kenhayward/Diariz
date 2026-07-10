namespace Diariz.Domain.Migrations;

/// <summary>Puts every section in its owner's personal room. Mints a missing personal room first - a user
/// created between phases (before anything called RoomScope) has none. One-way, so it lives in the
/// AddSectionRoomId migration, not the startup seeder; __EFMigrationsHistory runs it exactly once per database.
/// Idempotent (re-running the UPDATE is a no-op once RoomId is set).</summary>
public static class SectionRoomBackfill
{
    public const string Sql = PersonalRoomBackfill.Sql + """

        UPDATE "Sections" s
        SET "RoomId" = r."Id"
        FROM "Rooms" r
        WHERE r."OwnerUserId" = s."UserId" AND r."Kind" = 0;
        """;
}
