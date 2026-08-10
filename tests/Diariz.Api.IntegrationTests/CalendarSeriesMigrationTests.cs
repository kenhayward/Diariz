using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>The series-id backfill. A fresh migrated database has no links at all, so the rest of the suite
/// runs this SQL against zero rows and proves nothing. These build a scratch database at the previous schema,
/// insert links of each id shape a live instance actually holds, and roll forward.</summary>
[Collection(IntegrationCollection.Name)]
public class CalendarSeriesMigrationTests(ContainersFixture fx)
{
    /// <summary>The migration immediately before the series-id backfill - the schema a live instance is on today.</summary>
    private const string Before = "20260809145156_AddCalendarRecordingPreferences";
    private const string Target = "20260810142944_AddCalendarSeriesId";

    /// <summary>A scratch database on the shared Postgres container, migrated only as far as <see cref="Before"/>.</summary>
    private async Task<string> ScratchAtOldSchemaAsync()
    {
        var name = $"series_{Guid.NewGuid():N}";

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
    public async Task Backfill_DerivesTheSeriesFromEachIdShape()
    {
        var cs = await ScratchAtOldSchemaAsync();

        // A Google recurring instance: {masterId}_{stamp}.
        // An .ics recurring instance: our own MakeId, same shape, and the uid may itself contain '_'.
        // A one-off: no stamp suffix, so no series.
        // An Outlook link: resolved by joining the mirror on 'outlook:' || Id.
        await ExecAsync(cs, """
            INSERT INTO "AspNetUsers" ("Id","UserName","Email","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
            VALUES ('11111111-1111-1111-1111-111111111111','u@x.test','u@x.test',true,false,false,false,0);
            """);

        // Four Recordings - one per id shape under test.
        await ExecAsync(cs, """
            INSERT INTO "Recordings"
                ("Id","UserId","Title","BlobKey","ContentType","CreatedAt","DurationMs","Position","SizeBytes","Source","Status")
            VALUES
                ('22222222-2222-2222-2222-222222222221','11111111-1111-1111-1111-111111111111','Google recurring','blob1','audio/webm',now(),0,0,0,0,0),
                ('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','Ics recurring','blob2','audio/webm',now(),0,0,0,0,0),
                ('22222222-2222-2222-2222-222222222223','11111111-1111-1111-1111-111111111111','One off','blob3','audio/webm',now(),0,0,0,0,0),
                ('22222222-2222-2222-2222-222222222224','11111111-1111-1111-1111-111111111111','Outlook recurring','blob4','audio/webm',now(),0,0,0,0,0);
            """);

        // The OutlookCalendarSource the mirror row's SourceId FK requires.
        const string outlookSourceId = "66666666-6666-6666-6666-666666666661";
        await ExecAsync(cs, $"""
            INSERT INTO "OutlookCalendarSources"
                ("Id","UserId","DeviceId","DisplayName","Enabled","FutureDays","IncludeBody","LastEventCount","PastDays","SkipPrivate","CreatedAt")
            VALUES
                ('{outlookSourceId}','11111111-1111-1111-1111-111111111111','dev-1','My PC',true,14,false,0,7,false,now());
            """);

        // The OutlookCalendarEvents mirror row this test's Outlook link resolves against.
        const string outlookEventId = "33333333-3333-3333-3333-333333333331";
        await ExecAsync(cs, $"""
            INSERT INTO "OutlookCalendarEvents"
                ("Id","UserId","SourceId","SyncId","Uid","StartsAt","EndsAt","AllDay","BusyStatus","IsRecurring","Sensitivity","SyncedAt","SourceLastModified")
            VALUES
                ('{outlookEventId}','11111111-1111-1111-1111-111111111111','{outlookSourceId}','55555555-5555-5555-5555-555555555551',
                 'GLOBALSERIES#2026-08-10T09:00:00Z',now(),now(),false,0,true,0,now(),now());
            """);

        // The four RecordingCalendarLinks, one per EventId shape.
        await ExecAsync(cs, $"""
            INSERT INTO "RecordingCalendarLinks"
                ("RecordingId","EventId","CalendarId","StartsAt","EndsAt","LinkedManually","SyncedAt")
            VALUES
                ('22222222-2222-2222-2222-222222222221','abc_20260810T090000Z','primary',now(),now(),false,now()),
                ('22222222-2222-2222-2222-222222222222','my_uid@x.test_20260810T090000Z','primary',now(),now(),false,now()),
                ('22222222-2222-2222-2222-222222222223','plain-one-off','primary',now(),now(),false,now()),
                ('22222222-2222-2222-2222-222222222224','outlook:{outlookEventId}','primary',now(),now(),false,now());
            """);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Target);

        Assert.Equal("abc", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'abc_20260810T090000Z';"""));
        Assert.Equal("my_uid@x.test", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'my_uid@x.test_20260810T090000Z';"""));
        Assert.Null(await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'plain-one-off';"""));
        Assert.Equal("GLOBALSERIES", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" LIKE 'outlook:%';"""));
    }
}
