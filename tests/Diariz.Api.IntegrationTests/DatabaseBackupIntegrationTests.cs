using System.Diagnostics;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real <c>pg_dump</c>/<c>pg_restore</c> round-trip against an isolated throwaway database (so the
/// shared fixture schema is never touched). Skipped automatically when the Postgres client tools aren't on
/// the host PATH — CI is a self-hosted Windows runner where they may be absent, so this verifies for real
/// where possible without making the suite depend on the binaries.</summary>
[Collection(IntegrationCollection.Name)]
public class DatabaseBackupIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task DumpThenRestore_RoundTripsData()
    {
        if (!ToolOnPath("pg_dump") || !ToolOnPath("pg_restore"))
            return; // tools unavailable on this host — skip (covered by unit tests + manual verification)

        // A throwaway database on the same server, independent of the shared fixture DB.
        var admin = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString);
        var probeDb = $"probe_{Guid.NewGuid():N}";
        await Exec(admin.ConnectionString, $"CREATE DATABASE \"{probeDb}\"");
        var probe = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString) { Database = probeDb };
        try
        {
            await Exec(probe.ConnectionString,
                "CREATE TABLE probe (id int primary key, note text); INSERT INTO probe VALUES (1, 'hello');");

            var backup = new PgToolsDatabaseBackup(probe.ConnectionString);
            using var dump = new MemoryStream();
            await backup.DumpToAsync(dump);
            Assert.True(dump.Length > 0);

            // Mutate away from the dumped state...
            await Exec(probe.ConnectionString, "DROP TABLE probe;");

            // ...then restore should bring the table + row back exactly.
            dump.Position = 0;
            await backup.RestoreFromAsync(dump);

            var note = await Scalar(probe.ConnectionString, "SELECT note FROM probe WHERE id = 1");
            Assert.Equal("hello", note);
        }
        finally
        {
            // Force-drop the probe DB (terminate any lingering connections first).
            await Exec(admin.ConnectionString,
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{probeDb}';");
            await Exec(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{probeDb}\"");
        }
    }

    [Fact]
    public async Task MigrateToCurrent_FromAnEarlierMigration_RollsForwardKeepingData()
    {
        // A throwaway database, migrated only to the baseline, then rolled forward via the production
        // primitive (EfSchemaVersion.MigrateToCurrentAsync) - the second half of an older-backup restore.
        var admin = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString);
        var probeDb = $"probe_{Guid.NewGuid():N}";
        await Exec(admin.ConnectionString, $"CREATE DATABASE \"{probeDb}\"");
        var probe = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString) { Database = probeDb };
        try
        {
            var options = new DbContextOptionsBuilder<DiarizDbContext>()
                .UseNpgsql(probe.ConnectionString, o => o.UseVector())
                .Options;
            await using var db = new DiarizDbContext(options);

            var all = db.Database.GetMigrations().ToList();
            Assert.True(all.Count > 1, "need at least two migrations to prove a forward roll");
            var baseline = all.First();

            // Migrate to the baseline only.
            await db.GetService<IMigrator>().MigrateAsync(baseline);
            Assert.Equal(baseline, (await db.Database.GetAppliedMigrationsAsync()).Last());

            // A marker row in a table outside the EF model - it must survive the roll-forward untouched.
            await Exec(probe.ConnectionString,
                "CREATE TABLE probe_marker (id int primary key, note text); INSERT INTO probe_marker VALUES (1, 'keep');");

            // A column added by a later migration is absent at the baseline...
            Assert.Equal(0L, await ColumnCount(probe.ConnectionString, "UserSettings", "RecordingPlacementMode"));

            // Roll forward to the latest schema via the production primitive.
            await new EfSchemaVersion(db).MigrateToCurrentAsync();

            Assert.Equal(all.Last(), (await db.Database.GetAppliedMigrationsAsync()).Last()); // now at latest
            Assert.Equal(1L, await ColumnCount(probe.ConnectionString, "UserSettings", "RecordingPlacementMode")); // added
            Assert.Equal("keep", await Scalar(probe.ConnectionString, "SELECT note FROM probe_marker WHERE id = 1")); // data kept
        }
        finally
        {
            await Exec(admin.ConnectionString,
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{probeDb}';");
            await Exec(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{probeDb}\"");
        }
    }

    [Fact]
    public async Task Restore_ThenVectorRead_OnADataSourceWarmedBeforeTheRestore_Succeeds()
    {
        // Issue #783. `pg_restore --clean` drops and recreates the `vector` extension, so the type comes back
        // under a NEW oid. A data source that loaded its type info before the restore - which is every running
        // API - still maps the old oid, and the next vector read fails with "DataTypeName '-.-'" (login 500s in
        // prod). The data source must be warmed BEFORE the restore: one created afterwards would load the new oid
        // and could not reproduce this.
        if (!ToolOnPath("pg_dump") || !ToolOnPath("pg_restore"))
            return; // tools unavailable on this host — skip (see the class summary)

        var admin = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString);
        var probeDb = $"probe_{Guid.NewGuid():N}";
        await Exec(admin.ConnectionString, $"CREATE DATABASE \"{probeDb}\"");
        var probe = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString) { Database = probeDb };
        try
        {
            // Same options for every context, exactly as AddDbContext gives each request scope - so they share
            // one EF-managed data source and its type cache.
            var options = new DbContextOptionsBuilder<DiarizDbContext>()
                .UseNpgsql(probe.ConnectionString, o => o.UseVector())
                .Options;

            await using (var before = new DiarizDbContext(options))
            {
                await before.Database.MigrateAsync();
                Assert.Equal(new[] { 1f, 2f, 3f }, await ReadVector(before)); // warms the type cache
            }
            var oidBefore = await Scalar(probe.ConnectionString, "SELECT 'vector'::regtype::oid");

            var backup = new PgToolsDatabaseBackup(probe.ConnectionString);
            using var dump = new MemoryStream();
            await backup.DumpToAsync(dump);
            dump.Position = 0;
            await backup.RestoreFromAsync(dump);

            // The precondition that makes this a real reproduction, not a pass by luck.
            Assert.NotEqual(oidBefore, await Scalar(probe.ConnectionString, "SELECT 'vector'::regtype::oid"));

            // What the restore request does next, in its own scope (MaintenanceController.Restore).
            await using (var restoring = new DiarizDbContext(options))
                await new EfSchemaVersion(restoring).ReloadTypesAsync();

            // A later request scope on the same data source.
            await using var after = new DiarizDbContext(options);
            Assert.Equal(new[] { 1f, 2f, 3f }, await ReadVector(after));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec(admin.ConnectionString,
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{probeDb}';");
            await Exec(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{probeDb}\"");
        }
    }

    /// <summary>Reads a vector through the context's own connection, so it goes through the same EF-managed data
    /// source (and type cache) as the app's queries.</summary>
    private static async Task<float[]> ReadVector(DiarizDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT '[1,2,3]'::vector";
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return reader.GetFieldValue<Pgvector.Vector>(0).ToArray();
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<long> ColumnCount(string connectionString, string table, string column)
    {
        var n = await Scalar(connectionString,
            $"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'");
        return Convert.ToInt64(n);
    }

    private static async Task Exec(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static bool ToolOnPath(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p!.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
