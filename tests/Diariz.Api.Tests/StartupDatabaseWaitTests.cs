using Diariz.Api.Services;
using Npgsql;

namespace Diariz.Api.Tests;

/// <summary>
/// Waiting for Postgres at startup instead of dying on it.
///
/// <para>Measured on a real redeploy: the API reached its startup migration <b>0.9 seconds</b> before
/// Postgres finished starting, took a <c>57P03</c>, and the unhandled exception killed the process.
/// Docker restarted it five seconds later, so it looked like a blip - but every request in that window
/// failed, and the user saw an error for something that worked on retry.</para>
///
/// <para>The distinction this policy has to draw is between <b>not ready yet</b> and <b>not coming</b>.
/// Retrying forever would turn a misconfigured connection string into a silent hang, which is worse
/// than crashing: at least a crash says something.</para>
/// </summary>
public class StartupDatabaseWaitTests
{
    /// The Postgres codes that mean "ask again shortly". Anything else is a real fault.
    private static PostgresException Transient(string sqlState) =>
        new("the database system is starting up", "FATAL", "FATAL", sqlState);

    [Fact]
    public async Task ItRetriesWhileTheDatabaseIsStillStartingUp()
    {
        // 57P03 is exactly what the API took on the redeploy that prompted this.
        var attempts = 0;
        await StartupDatabaseWait.RunAsync(
            () =>
            {
                attempts++;
                if (attempts < 3) throw Transient("57P03");
                return Task.CompletedTask;
            },
            attempts: 10, delay: TimeSpan.Zero);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ItRetriesWhenTheDatabaseCannotBeReachedAtAll()
    {
        // The other shape of the same race: the API is up before Postgres is listening, so the connection
        // is refused rather than answered with a status. Both mean "too early", not "broken".
        var attempts = 0;
        await StartupDatabaseWait.RunAsync(
            () =>
            {
                attempts++;
                if (attempts < 2) throw new NpgsqlException("Connection refused");
                return Task.CompletedTask;
            },
            attempts: 10, delay: TimeSpan.Zero);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ItGivesUpRatherThanHangingForeverOnADatabaseThatIsNotComing()
    {
        // A wrong host or password is not a race, and waiting quietly on one is worse than failing: a
        // container stuck in a retry loop looks healthy from the outside and says nothing about why.
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            StartupDatabaseWait.RunAsync(
                () => { attempts++; throw Transient("57P03"); },
                attempts: 4, delay: TimeSpan.Zero));

        Assert.Equal(4, attempts);
        Assert.Equal("57P03", ex.SqlState);
    }

    [Fact]
    public async Task AFaultThatIsNotAboutAvailabilityFailsImmediately()
    {
        // A broken migration, a bad schema, an authentication failure - retrying those just delays the
        // report by the length of the backoff and buries the cause under repetitions.
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupDatabaseWait.RunAsync(
                () => { attempts++; throw new InvalidOperationException("the migration is wrong"); },
                attempts: 10, delay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AWrongPasswordIsNotTreatedAsATransientRace()
    {
        // 28P01 is invalid_password. It will never resolve by waiting, and treating it as transient would
        // turn a typo in the connection string into a slow, silent startup failure.
        var attempts = 0;
        await Assert.ThrowsAsync<PostgresException>(() =>
            StartupDatabaseWait.RunAsync(
                () => { attempts++; throw Transient("28P01"); },
                attempts: 10, delay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TheHappyPathRunsOnceAndDoesNotWait()
    {
        var attempts = 0;
        await StartupDatabaseWait.RunAsync(
            () => { attempts++; return Task.CompletedTask; },
            attempts: 10, delay: TimeSpan.FromMinutes(5));

        Assert.Equal(1, attempts);
    }
}
