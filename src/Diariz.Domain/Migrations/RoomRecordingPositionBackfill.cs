namespace Diariz.Domain.Migrations;

/// <summary>Seeds the new per-room <c>RoomRecordings.Position</c> from the legacy global
/// <c>Recordings.Position</c> for MAIN placements, so a room's existing manual order carries over. Shared
/// placements keep the column default (0); their newest-first <c>CreatedAt</c> tiebreak preserves today's order.
///
/// One-way, so it lives in the migration (runs once per database via __EFMigrationsHistory), not the seeder.
/// A plain UPDATE of an existing column - idempotent, so a test can run it twice.</summary>
public static class RoomRecordingPositionBackfill
{
    public const string Sql = """
        UPDATE "RoomRecordings" p
        SET "Position" = r."Position"
        FROM "Recordings" r
        WHERE p."RecordingId" = r."Id" AND p."IsMainRoom";
        """;
}
