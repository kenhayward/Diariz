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

        var drained = 0;
        while (sink.Reader.TryRead(out _)) drained++;
        Assert.Equal(ChannelLlmUsageSink.Capacity, drained);
    }

    [Fact]
    public void Dropped_StartsAtZero()
    {
        Assert.Equal(0, new ChannelLlmUsageSink().Dropped);
    }
}
