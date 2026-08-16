using Diariz.Api.Services;
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
    private static LlmCall Call() => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1,
        Kind = LlmCallKind.Tags, Model = "m", Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Success = true,
    };

    [Fact]
    public async Task DrainAsync_TakesAtMostMax_LeavingTheRestBuffered()
    {
        var sink = new ChannelLlmUsageSink();
        for (var i = 0; i < 5; i++) sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 3, CancellationToken.None);

        Assert.Equal(3, batch.Count);
        Assert.True(sink.Reader.TryRead(out _)); // the remainder is still there
    }

    [Fact]
    public async Task DrainAsync_ReturnsWhatIsAvailable_WithoutWaitingForMax()
    {
        // The writer flushes on a timer as well as on volume. If this blocked until `max` arrived, a
        // quiet system would never persist anything.
        var sink = new ChannelLlmUsageSink();
        sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 200, CancellationToken.None);

        Assert.Single(batch);
    }
}
