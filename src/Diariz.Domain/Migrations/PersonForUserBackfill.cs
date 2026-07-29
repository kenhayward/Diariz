namespace Diariz.Domain.Migrations;

/// <summary>Gives every pre-existing active user a <c>Person</c> row linked to their account, so the
/// directory starts out complete rather than filling in as people happen to sign in.
///
/// It lives in the AddPersonDirectory migration - not the startup seeder - because it must run EXACTLY ONCE
/// per database. __EFMigrationsHistory guarantees that, including for an upgrading deployment. A seeder runs
/// on every boot and would resurrect a person the administrator had since deleted, or undo a rename. Phase 1
/// made exactly this mistake with the role backfill; see <see cref="RoleToGroupBackfill"/>.
///
/// Idempotent anyway (the NOT EXISTS guard plus ON CONFLICT DO NOTHING, which the filtered unique index on
/// LinkedUserId backs up), so re-running it in a test cannot duplicate rows.
///
/// <para><b>Active users only</b> (Status 2 = UserStatus.Active). A Requested account has asked for access
/// and may never be granted it; an Invited one has not set a name yet. Provisioning either would fill the
/// directory with people who have never spoken and, in the Requested case, may never exist as colleagues at
/// all. They get their person at CompleteSetup, which is the moment they become Active.</para>
///
/// <para>Writes into the <c>SpeakerProfiles</c> table because that is where <c>Person</c> lives - the table
/// keeps its old name so older backup archives stay restorable. Note the two user columns mean different
/// things: <c>"UserId"</c> is who enrolled the person and is deliberately NULL here (nobody did - the system
/// provisioned them), while <c>"LinkedUserId"</c> is the account the person is.</para></summary>
public static class PersonForUserBackfill
{
    public const string Sql = """
        INSERT INTO "SpeakerProfiles"
            ("Id", "UserId", "RoomId", "LinkedUserId", "Name", "Email", "Title", "CompanyName", "Phone",
             "IsInternal", "VoiceprintOptOut", "SampleCount", "CreatedAt", "UpdatedAt")
        SELECT gen_random_uuid(),
               NULL,
               NULL,
               u."Id",
               COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Unknown'),
               u."Email",
               NULL, NULL, NULL,
               true,
               false,
               0,
               now(),
               now()
        FROM "AspNetUsers" u
        WHERE u."Status" = 2
          AND NOT EXISTS (SELECT 1 FROM "SpeakerProfiles" p WHERE p."LinkedUserId" = u."Id")
        ON CONFLICT DO NOTHING;

        COMMENT ON COLUMN "SpeakerProfiles"."UserId" IS
            'Who ENROLLED this person (provenance only, never filtered on). Not the account the person is - that is LinkedUserId.';

        COMMENT ON COLUMN "SpeakerProfiles"."LinkedUserId" IS
            'The account this person IS, or NULL for someone with no login. Unique among non-null values.';

        COMMENT ON TABLE "SpeakerProfiles" IS
            'The people directory (CLR type Person). Keeps its original table name because renaming it would force a backup-format bump and reject every older archive.';
        """;
}
