using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>The <c>AddRecordingTagStatus</c> migration runs a <c>DELETE ... USING</c> to collapse legacy
/// case-variant duplicates <b>before</b> creating the <c>(RecordingId, lower(Tag))</c> unique index - a live
/// instance that already has two differently-cased rows for the same tag on one recording would otherwise fail
/// to deploy (the index creation would abort). A fresh migrated database has zero <c>RecordingTags</c> rows, so
/// the rest of the suite runs this SQL against nothing and proves nothing about it. This builds a scratch
/// database at the previous schema, inserts a case-variant duplicate pair via raw SQL (the shape a live
/// instance could actually hold), and rolls forward - exactly what a real deploy does.</summary>
[Collection(IntegrationCollection.Name)]
public class RecordingTagStatusMigrationTests(ContainersFixture fx)
{
    /// <summary>The migration immediately before the status/index migration - the schema a live instance is on today.</summary>
    private const string Before = "20260811104743_OutlookNarrowSyncStamp";
    private const string Target = "20260814104629_AddRecordingTagStatus";

    /// <summary>A scratch database on the shared Postgres container, migrated only as far as <see cref="Before"/>.</summary>
    private async Task<string> ScratchAtOldSchemaAsync()
    {
        var name = $"tagstatus_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fx.PostgresConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString) { Database = name }.ToString();
        await using var db = Context(cs);
        await db.Database.GetService<IMigrator>().MigrateAsync(Before);
        return cs;
    }

    private static DiarizDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options);

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [Fact]
    public async Task Migration_CollapsesACaseVariantDuplicate_KeepingTheLowerOrdinal_AndTheIndexThenRejectsAFreshOne()
    {
        var cs = await ScratchAtOldSchemaAsync();

        const string userId = "11111111-1111-1111-1111-111111111111";
        const string recordingId = "22222222-2222-2222-2222-222222222221";

        await ExecAsync(cs, $"""
            INSERT INTO "AspNetUsers" ("Id","UserName","Email","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
            VALUES ('{userId}','u@x.test','u@x.test',true,false,false,false,0);
            """);

        await ExecAsync(cs, $"""
            INSERT INTO "Recordings"
                ("Id","UserId","Title","BlobKey","ContentType","CreatedAt","DurationMs","Position","SizeBytes","Source","Status")
            VALUES
                ('{recordingId}','{userId}','Standup','blob1','audio/webm',now(),0,0,0,0,0);
            """);

        // A legacy case-variant duplicate pair on the SAME recording - exactly the shape the migration's
        // DELETE ... USING targets. "Metadata" (Ordinal 0) is the one to keep; "metadata" (Ordinal 1, a
        // higher Id lexically doesn't matter - Ordinal alone decides) is the duplicate to collapse away.
        const string keepId = "33333333-3333-3333-3333-333333333331";
        const string dropId = "33333333-3333-3333-3333-333333333332";
        await ExecAsync(cs, $"""
            INSERT INTO "RecordingTags" ("Id","RecordingId","Tag","Weight","Ordinal","CreatedAt")
            VALUES
                ('{keepId}','{recordingId}','Metadata',0.8,0,now()),
                ('{dropId}','{recordingId}','metadata',0.5,1,now());
            """);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Target);

        await using var after = Context(cs);
        var survivor = await after.RecordingTags.Where(t => t.RecordingId == Guid.Parse(recordingId)).ToListAsync();

        var tag = Assert.Single(survivor); // the duplicate did not survive the migration
        Assert.Equal(Guid.Parse(keepId), tag.Id); // the lower-Ordinal row is the one that was kept
        Assert.Equal("Metadata", tag.Tag);

        // The unique index now exists and is enforced: a fresh case-variant insert on the same recording fails.
        var exists = await ScalarAsync<bool>(cs, """
            SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'IX_RecordingTags_RecordingId_TagLower');
            """);
        Assert.True(exists);

        var violation = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"""
            INSERT INTO "RecordingTags" ("Id","RecordingId","Tag","Weight","Ordinal","CreatedAt")
            VALUES (gen_random_uuid(),'{recordingId}','METADATA',0.3,2,now());
            """));
        Assert.Equal("23505", violation.SqlState); // unique_violation
    }
}
