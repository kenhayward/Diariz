using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

/// <summary>Orphaned-job recovery against a real Redis. The reclaim leans on XPENDING's idle time and
/// delivery count and on XCLAIM's own min-idle guard, none of which a fake reproduces faithfully - and
/// getting them wrong fails in the two worst directions available: silently recovering nothing, or
/// stealing a job from a healthy worker and running it twice.</summary>
[Collection(IntegrationCollection.Name)]
public class StreamReclaimIntegrationTests(ContainersFixture fx)
{
    private const string Group = "workers";

    /// <summary>Read a message as <paramref name="consumer"/> and never ack it - exactly what a process
    /// killed mid-job leaves behind.</summary>
    private static async Task<string> AbandonAsync(IDatabase db, string stream, string consumer, string job)
    {
        await db.StreamAddAsync(stream, "job", job);
        var read = await db.StreamReadGroupAsync(stream, Group, consumer, ">", count: 1);
        return read[0].Id!;
    }

    private static async Task<IDatabase> FreshStreamAsync(ContainersFixture fx, string stream)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(stream);
        await db.StreamCreateConsumerGroupAsync(stream, Group, "0", createStream: true);
        return db;
    }

    [Fact]
    public async Task Reclaims_a_message_left_pending_by_a_dead_consumer()
    {
        var stream = $"reclaim-{Guid.NewGuid():N}";
        var db = await FreshStreamAsync(fx, stream);
        var id = await AbandonAsync(db, stream, "dead-worker", "{\"TranscriptionId\":\"t1\"}");

        // MinIdle is ten minutes in production; pass zero so the message counts as stale immediately.
        var claimed = await db.StreamClaimAsync(stream, Group, "live-worker", 0, [id]);

        Assert.Single(claimed);
        Assert.Equal(id, claimed[0].Id);
        Assert.Equal("{\"TranscriptionId\":\"t1\"}", (string)claimed[0]["job"]!);

        // And the live worker now owns it, so a second instance cannot also pick it up.
        var pending = await db.StreamPendingMessagesAsync(stream, Group, 10, RedisValue.Null);
        Assert.Equal("live-worker", pending.Single().ConsumerName);
    }

    [Fact]
    public async Task Does_not_steal_a_message_a_healthy_worker_is_still_working_on()
    {
        // The failure that would matter most: a long transcription's message looks idle for as long as
        // the job takes, and stealing it would run the whole thing twice on the GPU. XCLAIM's min-idle
        // is what prevents that, so this asserts the guard rather than trusting it.
        var stream = $"reclaim-{Guid.NewGuid():N}";
        var db = await FreshStreamAsync(fx, stream);
        var id = await AbandonAsync(db, stream, "busy-worker", "{\"TranscriptionId\":\"t2\"}");

        // The production threshold, against a message delivered a moment ago.
        var reclaimer = new StreamReclaimer();
        var entries = await reclaimer.ReclaimDueAsync(db, stream, Group, "thief", NullLogger.Instance);

        Assert.Empty(entries);
        var pending = await db.StreamPendingMessagesAsync(stream, Group, 10, RedisValue.Null);
        Assert.Equal("busy-worker", pending.Single().ConsumerName);
        Assert.Equal(id, pending.Single().MessageId.ToString());
    }

    [Fact]
    public async Task Reclaimer_returns_the_orphan_and_leaves_an_undelivered_message_alone()
    {
        var stream = $"reclaim-{Guid.NewGuid():N}";
        var db = await FreshStreamAsync(fx, stream);
        var orphan = await AbandonAsync(db, stream, "dead-worker", "{\"TranscriptionId\":\"orphan\"}");
        await db.StreamAddAsync(stream, "job", "{\"TranscriptionId\":\"unread\"}"); // never delivered

        // Zero idle so the just-abandoned message qualifies; every other rule is the production one.
        var reclaimer = new StreamReclaimer(minIdle: TimeSpan.Zero);
        var entries = await reclaimer.ReclaimDueAsync(db, stream, Group, "live", NullLogger.Instance);

        // The undelivered message is not pending at all, so recovery must not touch it - if it did, a job
        // waiting its turn would be run by a worker that never read it.
        Assert.Single(entries);
        Assert.Equal(orphan, entries[0].Id);
        Assert.Equal("{\"TranscriptionId\":\"orphan\"}", (string)entries[0]["job"]!);
    }

    [Fact]
    public async Task Throttles_so_a_second_call_straight_away_does_no_work()
    {
        var stream = $"reclaim-{Guid.NewGuid():N}";
        var db = await FreshStreamAsync(fx, stream);
        await AbandonAsync(db, stream, "dead-worker", "{\"TranscriptionId\":\"t3\"}");

        var reclaimer = new StreamReclaimer(minIdle: TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        Assert.NotEmpty(await reclaimer.ReclaimDueAsync(db, stream, Group, "live", NullLogger.Instance, now));
        Assert.Empty(await reclaimer.ReclaimDueAsync(db, stream, Group, "live", NullLogger.Instance, now));
    }

    [Fact]
    public async Task Abandons_a_message_that_has_already_killed_enough_workers()
    {
        var stream = $"reclaim-{Guid.NewGuid():N}";
        var db = await FreshStreamAsync(fx, stream);
        var id = await AbandonAsync(db, stream, "victim-0", "{\"TranscriptionId\":\"poison\"}");

        // Each claim counts as another delivery, so this walks it past the cap the way a succession of
        // dying workers would.
        for (var i = 1; i <= StreamReclaimer.MaxDeliveries; i++)
            await db.StreamClaimAsync(stream, Group, $"victim-{i}", 0, [id]);

        var reclaimer = new StreamReclaimer(minIdle: TimeSpan.Zero);
        var entries = await reclaimer.ReclaimDueAsync(db, stream, Group, "live", NullLogger.Instance);

        // Dropped rather than handed on: it is likelier the cause of the deaths than a casualty of them.
        Assert.Empty(entries);
        Assert.Empty(await db.StreamPendingMessagesAsync(stream, Group, 10, RedisValue.Null));
    }
}
