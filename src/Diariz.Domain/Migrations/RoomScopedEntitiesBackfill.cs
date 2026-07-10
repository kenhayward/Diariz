namespace Diariz.Domain.Migrations;

/// <summary>Puts voiceprints, saved chats and personal meeting types into their owner's personal room. Mints a
/// missing personal room first (as in 2b/2c). Platform meeting types (UserId null) keep RoomId null. One-way,
/// so it lives in the AddRoomScopedEntities migration. Idempotent (re-running the UPDATEs is a no-op).</summary>
public static class RoomScopedEntitiesBackfill
{
    public const string Sql = PersonalRoomBackfill.Sql + """

        UPDATE "SpeakerProfiles" p SET "RoomId" = r."Id"
        FROM "Rooms" r WHERE r."OwnerUserId" = p."UserId" AND r."Kind" = 0;

        UPDATE "ChatSessions" c SET "RoomId" = r."Id"
        FROM "Rooms" r WHERE r."OwnerUserId" = c."UserId" AND r."Kind" = 0;

        UPDATE "MeetingTypes" m SET "RoomId" = r."Id"
        FROM "Rooms" r WHERE m."UserId" IS NOT NULL AND r."OwnerUserId" = m."UserId" AND r."Kind" = 0;
        """;
}
