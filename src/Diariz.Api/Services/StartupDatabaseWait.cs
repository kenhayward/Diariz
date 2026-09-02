using Npgsql;

namespace Diariz.Api.Services;

/// <summary>
/// Waits for Postgres to be available at startup instead of dying on it.
///
/// <para>The API migrates and seeds before it serves anything, and on a redeploy it can reach that block
/// before Postgres has finished starting. Measured on a real one: the API was <b>0.9 seconds</b> early,
/// took a <c>57P03</c>, and the unhandled exception killed the process. Docker restarted it five seconds
/// later, so it read as a blip - but every request in that window failed, and the person using the app
/// saw an error for something that then worked on retry.</para>
///
/// <para><c>depends_on: condition: service_healthy</c> does not close this. The healthcheck polls every
/// five seconds, so Postgres can pass a check and still be restarting when the API actually connects.
/// It narrows the window; only waiting removes it.</para>
///
/// <para>The distinction that matters is <b>not ready yet</b> versus <b>not coming</b>. Retrying
/// everything would turn a wrong password or a broken migration into a container that sits quietly in a
/// loop, looking healthy from the outside and saying nothing about why - worse than crashing, because a
/// crash at least reports something. So only availability faults are waited on; everything else fails
/// at once, with its own error, unburied by repetitions.</para>
/// </summary>
public static class StartupDatabaseWait
{
    /// <summary>Postgres states that mean "ask again shortly" rather than "this is broken".</summary>
    private static readonly HashSet<string> Transient = new(StringComparer.Ordinal)
    {
        "57P03",   // cannot_connect_now - the database system is starting up. The observed one.
        "53300",   // too_many_connections - a restarting neighbour still holding the pool
        "08000",   // connection_exception
        "08006",   // connection_failure
        "08001",   // sqlclient_unable_to_establish_sqlconnection
        "08004",   // sqlserver_rejected_establishment_of_sqlconnection
    };

    /// <param name="attempts">Bounded on purpose - see the note above about failing loudly.</param>
    public static async Task RunAsync(
        Func<Task> work, int attempts = 30, TimeSpan? delay = null, ILogger? logger = null,
        CancellationToken ct = default)
    {
        var wait = delay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (Exception e) when (attempt < attempts && IsUnavailable(e))
            {
                logger?.LogInformation(
                    "Database not ready yet (attempt {Attempt}/{Attempts}): {Reason}. Waiting {Delay}s.",
                    attempt, attempts, e.Message, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }
    }

    /// <summary>Whether this is the database being unavailable rather than something being wrong with it.
    ///
    /// <para>An <see cref="NpgsqlException"/> that is not a <see cref="PostgresException"/> is the server
    /// not answering at all - refused, reset, timed out - which at startup means "too early" just as much
    /// as a <c>57P03</c> does. A <c>PostgresException</c> is the server answering with a state, and only
    /// the availability states qualify: an authentication failure will never resolve by waiting.</para>
    /// </summary>
    private static bool IsUnavailable(Exception e) => e switch
    {
        PostgresException pg => Transient.Contains(pg.SqlState),
        NpgsqlException => true,
        _ => false,
    };
}
