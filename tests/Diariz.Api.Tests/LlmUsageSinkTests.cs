using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class LlmUsageSinkTests
{
    private static LlmCall Call(int seq = 1) => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = seq,
        Kind = LlmCallKind.Summarize, Model = "m", Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Success = true,
    };

    [Fact]
    public void Record_MakesTheCallReadable()
    {
        var sink = new ChannelLlmUsageSink();
        var call = Call();

        sink.Record(call);

        Assert.True(sink.Reader.TryRead(out var read));
        Assert.Equal(call.Id, read!.Id);
    }

    [Fact]
    public void Record_NeverBlocks_AndDropsOldestWhenFull()
    {
        // The sink sits on the LLM call path. It must never block or throw: a monitoring feature that
        // can stall a summary is worse than a monitoring feature with gaps.
        var sink = new ChannelLlmUsageSink();
        for (var i = 0; i < ChannelLlmUsageSink.Capacity + 50; i++) sink.Record(Call(i + 1));

        Assert.Equal(50, sink.Dropped);

        var drained = new List<int>();
        while (sink.Reader.TryRead(out var call)) drained.Add(call!.Sequence);
        Assert.Equal(ChannelLlmUsageSink.Capacity, drained.Count);

        // The survivors must be the newest Capacity records (51..10050), not the oldest (1..10000) -
        // this is what distinguishes DropOldest from DropWrite/DropNewest, all of which report the
        // same Dropped count and drained count.
        Assert.Equal(51, drained[0]);
        Assert.Equal(ChannelLlmUsageSink.Capacity + 50, drained[^1]);
        Assert.DoesNotContain(1, drained);
    }

    [Fact]
    public void Dropped_StartsAtZero()
    {
        Assert.Equal(0, new ChannelLlmUsageSink().Dropped);
    }
}

public class LlmUsageBatchTests
{
    // Small but non-zero, so the coalescing test has a real window to land a second Record() in without
    // making every drain test pay a large real-time delay.
    private static readonly TimeSpan FastFlush = TimeSpan.FromMilliseconds(20);

    private static LlmCall Call() => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1,
        Kind = LlmCallKind.Tags, Model = "m", Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Success = true,
    };

    // Timeout: a regression to a spin-wait (e.g. DrainAsync never returning, or the flush delay looping)
    // must fail the test outright rather than hang the run forever.
    [Fact(Timeout = 5000)]
    public async Task DrainAsync_TakesAtMostMax_LeavingTheRestBuffered()
    {
        var sink = new ChannelLlmUsageSink();
        for (var i = 0; i < 5; i++) sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 3, FastFlush, CancellationToken.None);

        Assert.Equal(3, batch.Count);
        Assert.True(sink.Reader.TryRead(out _)); // the remainder is still there
    }

    [Fact(Timeout = 5000)]
    public async Task DrainAsync_ReturnsWhatIsAvailable_WithoutWaitingForMax()
    {
        // The writer flushes on a timer as well as on volume. If this blocked until `max` arrived, a
        // quiet system would never persist anything.
        var sink = new ChannelLlmUsageSink();
        sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 200, FastFlush, CancellationToken.None);

        Assert.Single(batch);
    }

    [Fact(Timeout = 5000)]
    public async Task DrainAsync_CoalescesRecordsThatArriveWithinTheFlushWindow()
    {
        // This is the batching the class doc promises ("every ~2 seconds or 200 rows"): a record that
        // shows up while the writer is already waiting to flush must land in the SAME batch, not force a
        // second scope-create + settings-query + SaveChanges round trip.
        var sink = new ChannelLlmUsageSink();
        sink.Record(Call());
        var flushWindow = TimeSpan.FromMilliseconds(200);

        var drainTask = LlmUsageBatch.DrainAsync(sink.Reader, max: 200, flushWindow, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(40)); // well inside the flush window
        sink.Record(Call());

        var batch = await drainTask;

        Assert.Equal(2, batch.Count);
    }

    [Fact(Timeout = 5000)]
    public async Task DrainAsync_ZeroFlushWindow_DoesNotWaitBeforeDraining()
    {
        // TimeSpan.Zero opts out of coalescing entirely - kept as an explicit case so "no wait when the
        // window is zero" doesn't silently regress into always waiting.
        var sink = new ChannelLlmUsageSink();
        sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 200, TimeSpan.Zero, CancellationToken.None);

        Assert.Single(batch);
    }

    [Fact]
    public async Task PersistAsync_DiscardsTheBatch_WhenLoggingIsDisabled()
    {
        // The master switch (PlatformSettings.LlmUsageLoggingEnabled) is enforced here, not in the
        // handler - this is the test that actually exercises that gate.
        using var db = TestDb.Create();
        var batch = new List<LlmCall> { Call() };

        var saved = await LlmUsageBatch.PersistAsync(db, batch, loggingEnabled: false, CancellationToken.None);

        Assert.Equal(0, saved);
        Assert.Empty(db.LlmCalls);
    }

    [Fact]
    public async Task PersistAsync_SavesTheBatch_WhenLoggingIsEnabled()
    {
        using var db = TestDb.Create();
        var batch = new List<LlmCall> { Call() };

        var saved = await LlmUsageBatch.PersistAsync(db, batch, loggingEnabled: true, CancellationToken.None);

        Assert.Equal(1, saved);
        Assert.Single(db.LlmCalls);
    }
}
