using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Parsing of streamed chat chunks: content deltas, tool-call fragments and finish reasons.</summary>
public class ChatStreamChunkTests
{
    [Fact]
    public void ContentDelta_IsExtracted()
    {
        var d = ChatStreamClient.ParseStreamChunk(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}", out var done);
        Assert.False(done);
        Assert.Equal("Hello", d!.Content);
        Assert.Null(d.ToolCalls);
        Assert.Null(d.FinishReason);
    }

    [Fact]
    public void Done_SetsDoneFlag()
    {
        var d = ChatStreamClient.ParseStreamChunk("data: [DONE]", out var done);
        Assert.True(done);
        Assert.Null(d);
    }

    [Fact]
    public void NonDataOrBlankLine_IsIgnored()
    {
        Assert.Null(ChatStreamClient.ParseStreamChunk("", out _));
        Assert.Null(ChatStreamClient.ParseStreamChunk(": keep-alive", out _));
        Assert.Null(ChatStreamClient.ParseStreamChunk("event: ping", out _));
    }

    [Fact]
    public void ToolCallFragment_IsExtracted()
    {
        var line = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
                   "\"function\":{\"name\":\"who_said_that\",\"arguments\":\"{\\\"phr\"}}]}}]}";
        var d = ChatStreamClient.ParseStreamChunk(line, out var done);
        Assert.False(done);
        Assert.Null(d!.Content);
        var frag = Assert.Single(d.ToolCalls!);
        Assert.Equal(0, frag.Index);
        Assert.Equal("call_1", frag.Id);
        Assert.Equal("who_said_that", frag.Name);
        Assert.Equal("{\"phr", frag.Arguments);
    }

    [Fact]
    public void FinishReason_IsExtracted()
    {
        var d = ChatStreamClient.ParseStreamChunk(
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}", out _);
        Assert.Equal("tool_calls", d!.FinishReason);
    }

    [Fact]
    public void EmptyDelta_ReturnsNull()
    {
        Assert.Null(ChatStreamClient.ParseStreamChunk(
            "data: {\"choices\":[{\"delta\":{}}]}", out _));
    }

    [Fact]
    public void Malformed_ReturnsNull()
    {
        Assert.Null(ChatStreamClient.ParseStreamChunk("data: {not json", out var done));
        Assert.False(done);
    }
}
