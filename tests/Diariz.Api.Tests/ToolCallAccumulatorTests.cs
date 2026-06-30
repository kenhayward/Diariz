using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ToolCallAccumulatorTests
{
    [Fact]
    public void StitchesFragments_ByIndex()
    {
        var acc = new ToolCallAccumulator();
        acc.Add([new ToolCallFragment(0, "call_1", "who_said_that", "{\"phrase\":")]);
        acc.Add([new ToolCallFragment(0, null, null, "\"budget\"}")]);

        Assert.True(acc.HasCalls);
        var call = Assert.Single(acc.Build());
        Assert.Equal("call_1", call.Id);
        Assert.Equal("who_said_that", call.Name);
        Assert.Equal("{\"phrase\":\"budget\"}", call.Arguments);
    }

    [Fact]
    public void HandlesParallelCalls_DistinctIndexes()
    {
        var acc = new ToolCallAccumulator();
        acc.Add([
            new ToolCallFragment(0, "a", "who_said_that", "{}"),
            new ToolCallFragment(1, "b", "list_recordings", "{}"),
        ]);

        var calls = acc.Build();
        Assert.Equal(2, calls.Count);
        Assert.Equal("who_said_that", calls[0].Name);
        Assert.Equal("list_recordings", calls[1].Name);
    }

    [Fact]
    public void NoFragments_HasNoCalls()
    {
        var acc = new ToolCallAccumulator();
        acc.Add(null);
        Assert.False(acc.HasCalls);
        Assert.Empty(acc.Build());
    }

    [Fact]
    public void DropsCallsMissingAName()
    {
        var acc = new ToolCallAccumulator();
        acc.Add([new ToolCallFragment(0, "id", null, "{}")]); // no name ever arrived
        Assert.True(acc.HasCalls);
        Assert.Empty(acc.Build());
    }
}
