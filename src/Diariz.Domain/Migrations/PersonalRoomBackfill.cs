namespace Diariz.Domain.Migrations;

/// <summary>Gives every pre-existing user a Personal room, owned by them, with every room permission.
///
/// It lives in the AddRooms migration - not the startup seeder - because it must run EXACTLY ONCE per
/// database. __EFMigrationsHistory guarantees that, including for an upgrading deployment. A seeder runs on
/// every boot and would silently recreate a room the user had since changed. Phase 1 made exactly this mistake
/// with the role backfill; see <see cref="RoleToGroupBackfill"/>.
///
/// Idempotent anyway (the NOT EXISTS guard plus ON CONFLICT DO NOTHING), so re-running it in a test cannot
/// duplicate rows. That is a safety net, not a licence to run it on every boot.
///
/// Magic numbers, all append-only enum values: Kind 0 = RoomKind.Personal; PrincipalType 0 =
/// RoomPrincipalType.User; Permissions 63 = every RoomPermission flag (1|2|4|8|16|32).</summary>
public static class PersonalRoomBackfill
{
    public const string Sql = """
        INSERT INTO "Rooms" ("Id", "Name", "Description", "Icon", "Color", "Kind", "OwnerUserId", "CreatedAt")
        SELECT gen_random_uuid(),
               COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal'),
               NULL, NULL, NULL, 0, u."Id", now()
        FROM "AspNetUsers" u
        WHERE NOT EXISTS (SELECT 1 FROM "Rooms" r WHERE r."OwnerUserId" = u."Id")
        ON CONFLICT DO NOTHING;

        INSERT INTO "RoomMembers" ("RoomId", "PrincipalType", "PrincipalId", "Permissions")
        SELECT r."Id", 0, r."OwnerUserId", 63
        FROM "Rooms" r
        WHERE r."Kind" = 0 AND r."OwnerUserId" IS NOT NULL
        ON CONFLICT DO NOTHING;
        """;
}
