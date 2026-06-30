using System.Diagnostics;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
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
