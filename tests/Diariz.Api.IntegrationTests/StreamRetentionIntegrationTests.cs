using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

/// <summary>Trimming the job streams against a real Redis (issue #818) - the only place the XTRIM wire
/// behaviour can be proven, since the trim is issued as a raw command and nothing about it is visible to a
/// fake.
///
/// <para>The second test is the one that matters. An entry that is still <b>pending</b> holds the only copy
/// of its payload: <c>StreamReclaimer</c> reads it back before acking an abandoned message, and its
/// <c>if (entry.Length &gt; 0)</c> guard means a trimmed entry does not throw - the handler never runs, the
/// recording is never settled, and it sits in <c>Transcribing</c> forever. So trimming too much is a worse
/// failure than not trimming at all, and that has to be pinned rather than reasoned about.</para></summary>
[Collection(IntegrationCollection.Name)]
public class StreamRetentionIntegrationTests(ContainersFixture fx)
{
    private const string Group = "testers";

    [Fact]
    public async Task EntriesOlderThanTheWindow_AreTrimmedAndRecentOnesKept()
    {
        var (db, key) = await Stream();
        var now = DateTimeOffset.UtcNow;
        var old = await Add(db, key, now - StreamRetention.Window - TimeSpan.FromDays(1));
        var recent = await Add(db, key, now - TimeSpan.FromHours(1));

        await StreamRetention.TrimAsync(db, [key], now);

        Assert.Empty(await db.StreamRangeAsync(key, old, old));
        Assert.Single(await db.StreamRangeAsync(key, recent, recent));
    }

    /// <summary>A job still in flight keeps its payload, even unacknowledged. This is the property that
    /// makes the seven-day window the right call rather than an arbitrary one.</summary>
    [Fact]
    public async Task PendingEntryInsideTheWindow_KeepsItsPayload()
    {
        var (db, key) = await Stream();
        var now = DateTimeOffset.UtcNow;
        var id = await Add(db, key, now - TimeSpan.FromHours(2));
        await db.StreamCreateConsumerGroupAsync(key, Group, "0-0");
        var delivered = await db.StreamReadGroupAsync(key, Group, "consumer-1", ">", 10);
        Assert.Single(delivered);

        await StreamRetention.TrimAsync(db, [key], now);

        var pending = await db.StreamPendingMessagesAsync(key, Group, 10, "consumer-1");
        Assert.Single(pending);
        var readBack = await db.StreamRangeAsync(key, id, id);
        Assert.Single(readBack);
        Assert.Equal("payload", (string?)readBack[0].Values[0].Value);
    }

    /// <summary>Several streams in one pass, which is how the worker calls it - and the returned count is
    /// the total, since the worker logs one line rather than twelve.</summary>
    [Fact]
    public async Task TrimsEveryStreamItIsGiven()
    {
        var (db, first) = await Stream();
        var (_, second) = await Stream();
        var now = DateTimeOffset.UtcNow;
        var stale = now - StreamRetention.Window - TimeSpan.FromDays(1);
        await Add(db, first, stale);
        await Add(db, second, stale);

        var removed = await StreamRetention.TrimAsync(db, [first, second], now);

        Assert.Equal(2, removed);
        Assert.Equal(0, await db.StreamLengthAsync(first));
        Assert.Equal(0, await db.StreamLengthAsync(second));
    }

    private async Task<(IDatabase Db, string Key)> Stream()
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        return (mux.GetDatabase(), $"test-stream-{Guid.NewGuid():N}-jobs");
    }

    /// <summary>Adds an entry with an explicit ID, since a stream ID is a millisecond timestamp and that is
    /// what the trim compares against - letting Redis assign one would only ever produce "now".</summary>
    private static async Task<RedisValue> Add(IDatabase db, string key, DateTimeOffset at) =>
        await db.StreamAddAsync(key, "job", "payload", messageId: $"{at.ToUnixTimeMilliseconds()}-0");
}
