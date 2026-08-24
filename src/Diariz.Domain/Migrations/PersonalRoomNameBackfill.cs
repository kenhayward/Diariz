namespace Diariz.Domain.Migrations;

/// <summary>Points every Personal room's name back at its owner's display name.
///
/// A Personal room was named once, at creation, and nothing ever re-synced it - so renaming yourself left the
/// room reading whatever your name had been the day the room was minted. A production account sat under the
/// seeded "Platform Administrator" long after being renamed.
///
/// It lives in a migration, not the startup seeder, because a seeder runs on every boot; see
/// <see cref="PersonalRoomBackfill"/> for the same argument. It happens to be idempotent (the UPDATE matches
/// nothing once the names agree), which is a safety net, not a licence to run it on every boot.
///
/// The COALESCE mirrors <c>RoomScope.Display</c> exactly - keep the two in step. Magic number: Kind 0 =
/// RoomKind.Personal.</summary>
public static class PersonalRoomNameBackfill
{
    public const string Sql = """
        UPDATE "Rooms" r
        SET "Name" = COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal')
        FROM "AspNetUsers" u
        WHERE r."OwnerUserId" = u."Id"
          AND r."Kind" = 0
          AND r."Name" IS DISTINCT FROM COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal');
        """;
}
