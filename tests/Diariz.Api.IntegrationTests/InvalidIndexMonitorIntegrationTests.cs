using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>The catalog query behind the invalid-index alert (issue #822), against real Postgres - the only
/// place it can be proven, since `pg_index` and `pg_stat_progress_create_index` do not exist under the
/// in-memory provider and the whole point is what the catalog actually reports.
///
/// <para>The invalid index here is made the way the real one was: a `CREATE UNIQUE INDEX CONCURRENTLY` that
/// fails on duplicate rows. That leaves exactly the state a failed `REINDEX CONCURRENTLY` leaves - an index
/// Postgres keeps, ignores for queries, and still maintains on writes - rather than a state faked by writing
/// to the catalog, which would prove only that the test can write to the catalog.</para></summary>
[Collection(IntegrationCollection.Name)]
public class InvalidIndexMonitorIntegrationTests(ContainersFixture fx)
{
    /// <summary>A failed concurrent build leaves an invalid index, and the query finds it and reports no live
    /// build for it - which is what makes it alertable rather than ignorable.</summary>
    [Fact]
    public async Task FailedConcurrentBuild_IsFoundAsInvalidAndNotBuilding()
    {
        var table = $"t_{Guid.NewGuid():N}";
        var index = $"ix_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(fx.PostgresConnectionString);
        await conn.OpenAsync();
        await Exec(conn, $"CREATE TABLE \"{table}\" (v int);");
        await Exec(conn, $"INSERT INTO \"{table}\" VALUES (1), (1);");

        // Fails on the duplicate, and deliberately leaves the half-built index behind.
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(conn, $"CREATE UNIQUE INDEX CONCURRENTLY \"{index}\" ON \"{table}\" (v);"));
        Assert.Equal("23505", failure.SqlState);

        var found = await InvalidIndexMonitor.QueryAsync(conn, default);

        var mine = Assert.Single(found, i => i.Name == index);
        Assert.False(mine.BuildInProgress);
    }

    /// <summary>A healthy database reports nothing. Without this the query could be returning every index in
    /// the catalog and the test above would still pass.</summary>
    [Fact]
    public async Task ValidIndex_IsNotReported()
    {
        var table = $"t_{Guid.NewGuid():N}";
        var index = $"ix_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(fx.PostgresConnectionString);
        await conn.OpenAsync();
        await Exec(conn, $"CREATE TABLE \"{table}\" (v int);");
        await Exec(conn, $"INSERT INTO \"{table}\" VALUES (1), (2);");
        await Exec(conn, $"CREATE UNIQUE INDEX CONCURRENTLY \"{index}\" ON \"{table}\" (v);");

        var found = await InvalidIndexMonitor.QueryAsync(conn, default);

        Assert.DoesNotContain(found, i => i.Name == index);
    }

    /// <summary>The app's own schema is clean, which is both a sanity check on the query's filters and the
    /// assertion that would fail if a migration ever shipped a broken concurrent build.</summary>
    [Fact]
    public async Task MigratedSchema_HasNoInvalidIndexes()
    {
        await using var db = fx.CreateDbContext();
        await using var conn = new NpgsqlConnection(fx.PostgresConnectionString);
        await conn.OpenAsync();

        var found = await InvalidIndexMonitor.QueryAsync(conn, default);

        Assert.DoesNotContain(found, i => i.Name.StartsWith("IX_", StringComparison.Ordinal));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
