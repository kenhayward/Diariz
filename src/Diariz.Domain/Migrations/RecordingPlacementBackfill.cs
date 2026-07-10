namespace Diariz.Domain.Migrations;

/// <summary>Gives every existing recording its main placement: one RoomRecordings row in its recorder's Personal
/// room, carrying the folder the recording was filed in.
///
/// It re-runs <see cref="PersonalRoomBackfill"/> first, and that is not belt-and-braces. Phase 2a gave rooms
/// only to the users who existed then, and RoomScope creates them lazily - but nothing called RoomScope before
/// this phase, so any user created between the two deploys has NO personal room and may already own recordings.
/// Without the mint, their recordings are silently left unplaced and disappear from their list.
///
/// One-way, and therefore in the migration rather than the seeder: __EFMigrationsHistory runs it exactly once
/// per database, including for an upgrading deployment. Idempotent anyway (NOT EXISTS + ON CONFLICT), so a test
/// can run it twice.
///
/// `true` = IsMainRoom. SharedByUserId / SharedAt stay NULL: nobody shared a recording into its own home, and
/// the CK_RoomRecordings_MainRoomHasNoSharer check constraint enforces it.</summary>
public static class RecordingPlacementBackfill
{
    public const string Sql = PersonalRoomBackfill.Sql + """

        INSERT INTO "RoomRecordings" ("RoomId", "RecordingId", "IsMainRoom", "SectionId", "SharedByUserId", "SharedAt")
        SELECT room."Id", rec."Id", true, rec."SectionId", NULL, NULL
        FROM "Recordings" rec
        JOIN "Rooms" room ON room."OwnerUserId" = rec."UserId" AND room."Kind" = 0
        WHERE NOT EXISTS (
            SELECT 1 FROM "RoomRecordings" p WHERE p."RecordingId" = rec."Id" AND p."IsMainRoom"
        )
        ON CONFLICT DO NOTHING;
        """;
}
