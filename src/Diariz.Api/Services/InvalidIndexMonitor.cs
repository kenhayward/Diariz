using System.Data.Common;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Finds indexes Postgres is maintaining but will never use, and says so (issue #822).
///
/// <para>An index with <c>indisvalid = false</c> is "ignored for queries, while it may still consume update
/// overhead" - so a failed concurrent build leaves a permanent tax on every write with no compensating
/// benefit. On an HNSW index over <c>TranscriptChunk</c> that tax is large, since HNSW inserts are among the
/// more expensive index writes there are. It happened on production on 2026-10-03 after a REINDEX died, and
/// survived a full stack rebuild unnoticed: nothing reported it, no query plan mentions it, and the symptom
/// a person eventually sees is "transcription got slower".</para>
///
/// <para>Deliberately not exposed on <c>GET /health</c>, which is unauthenticated and where index names
/// would disclose schema. The finding goes to the log at Error level, which the Sentry/GlitchTip integration
/// turns into an alert - the point being that it reaches someone without anyone thinking to look.</para></summary>
public static class InvalidIndexMonitor
{
    /// <summary>Every invalid index in the user schemas, and whether Postgres currently lists a build for it.
    ///
    /// <para>The <c>EXISTS</c> against <c>pg_stat_progress_create_index</c> is the whole reason this is a
    /// query rather than a one-liner: without it there is no way to tell a deliberate concurrent build, which
    /// is invalid by design until it finishes, from the remains of one that failed. The catalog schemas are
    /// excluded because an invalid index in <c>pg_catalog</c> is not something this application put there or
    /// can act on.</para></summary>
    public static async Task<IReadOnlyList<IndexState>> QueryAsync(DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname AS name,
                   EXISTS (SELECT 1 FROM pg_stat_progress_create_index p
                           WHERE p.index_relid = c.oid) AS building
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT i.indisvalid
              AND n.nspname NOT IN ('pg_catalog', 'pg_toast', 'information_schema')
            ORDER BY c.relname
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var found = new List<IndexState>();
        while (await reader.ReadAsync(ct))
            found.Add(new IndexState(reader.GetString(0), reader.GetBoolean(1)));
        return found;
    }

    /// <summary>One pass: query, decide, log. Returns what the next pass should compare against, so the
    /// caller never derives that set a second time.</summary>
    public static async Task<IReadOnlySet<string>> RunAsync(
        DiarizDbContext db, IReadOnlySet<string> seenLastCheck, ILogger logger, CancellationToken ct = default)
    {
        var decision = InvalidIndexRules.Decide(
            await QueryAsync(db.Database.GetDbConnection(), ct), seenLastCheck);

        if (decision.Report.Count > 0)
            logger.LogError(
                "Invalid database index(es) found on two consecutive checks with no build running: {Indexes}. " +
                "An invalid index is ignored by every query plan but is still maintained on every write, so " +
                "it is pure cost. This is what a failed CREATE INDEX / REINDEX CONCURRENTLY leaves behind. " +
                "Drop it (DROP INDEX CONCURRENTLY), or re-run the reindex that failed.",
                string.Join(", ", decision.Report));

        return decision.Remember;
    }
}

/// <summary>Runs the check hourly. Singleton host, so it opens a DI scope per pass.
///
/// <para>Hourly rather than at startup only: the state arises while the application is running, from a
/// reindex an operator ran by hand. Combined with the two-consecutive-checks rule in
/// <see cref="InvalidIndexRules"/>, an alert therefore appears about an hour after the damage, which is the
/// price of not crying wolf over a concurrent build that is merely slow.</para></summary>
public class InvalidIndexMonitorWorker(IServiceProvider services, ILogger<InvalidIndexMonitorWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlySet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();

                // pg_index and pg_stat_progress_create_index are Postgres-only. Under any other provider
                // (the in-memory one the unit host uses) there is nothing to ask and nothing to report.
                if (db.Database.IsNpgsql())
                    seen = await InvalidIndexMonitor.RunAsync(db, seen, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Never let a failed check stop the loop: the next pass is an hour away and costs nothing.
                logger.LogWarning(e, "Invalid-index check failed; will try again in {Interval}.", CheckInterval);
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
