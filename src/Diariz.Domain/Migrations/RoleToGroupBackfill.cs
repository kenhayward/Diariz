namespace Diariz.Domain.Migrations;

/// <summary>The one-way move of Identity role holders into the seeded groups, run by the AddUserGroups
/// migration.
///
/// It lives in the migration - not in the startup seeder - because it must run EXACTLY ONCE per database.
/// __EFMigrationsHistory guarantees that, including for a deployment upgrading from a pre-groups schema. A
/// seeder runs on every boot, and would silently re-promote any user who had been demoted since, because the
/// legacy AspNetUserRoles row still names them an Administrator. Authority now lives in UserGroupMembers;
/// AspNetUserRoles is a historical record from here on.
///
/// Idempotent anyway (ON CONFLICT DO NOTHING) so re-running it can never duplicate a row, which keeps it
/// testable. That is a safety net, not a licence to run it on every boot.</summary>
public static class RoleToGroupBackfill
{
    /// <summary>Fixed ids so the groups are stable across databases and the SQL below can reference them.</summary>
    public const string PlatformAdminsGroupId = "9d3d8f3f-0f5a-4a1e-9d5a-0b0f5e6a1c01";
    public const string AdminsGroupId = "9d3d8f3f-0f5a-4a1e-9d5a-0b0f5e6a1c02";

    /// <summary>Administrators deliberately lacks ManagePlatform (4): that flag confers backup/restore and
    /// platform-settings writes, which the Administrator role has never had. 7 = all three, 3 = rooms+users.</summary>
    public const string Sql = $"""
        INSERT INTO "UserGroups" ("Id", "Name", "Description", "Icon", "Color", "Permissions", "IsSystem")
        VALUES
            ('{PlatformAdminsGroupId}', 'Platform Administrators', NULL, NULL, NULL, 7, TRUE),
            ('{AdminsGroupId}',         'Administrators',          NULL, NULL, NULL, 3, FALSE)
        ON CONFLICT ("Name") DO NOTHING;

        INSERT INTO "UserGroupMembers" ("GroupId", "UserId")
        SELECT g."Id", ur."UserId"
        FROM "AspNetUserRoles" ur
        JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
        JOIN "UserGroups" g ON g."Name" = CASE r."Name"
            WHEN 'PlatformAdministrator' THEN 'Platform Administrators'
            WHEN 'Administrator'         THEN 'Administrators'
        END
        WHERE r."Name" IN ('PlatformAdministrator', 'Administrator')
        ON CONFLICT DO NOTHING;
        """;
}
