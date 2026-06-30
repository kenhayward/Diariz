using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests;

public class ChatToolOrchestratorTests
{
    private static readonly SummarizationRequestConfig Cfg = new("https://llm.test/v1", "sk", "m", 60);
    private static readonly IReadOnlyList<ChatMessage> Seed = [new("user", "who said budget?")];

    private static async Task<List<ChatEvent>> Run(
        FakeChatStreamClient chat, IReadOnlyList<IChatTool> tools)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in new ChatToolOrchestrator(chat)
            .RunAsync(Cfg, Seed, tools, new ChatToolContext(Guid.NewGuid(), [])))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task NoTools_StreamsContentOnce()
    {
        var chat = new FakeChatStreamClient { Tokens = ["Hello", " world"] };
        var events = await Run(chat, []);

        Assert.Equal(1, chat.Calls);
        Assert.All(events, e => Assert.IsType<ChatTokenEvent>(e));
        Assert.Equal("Hello world", string.Concat(events.Cast<ChatTokenEvent>().Select(t => t.Value)));
    }

    [Fact]
    public async Task ToolCall_ExecutesTool_ThenStreamsAnswer()
    {
        var stub = new StubChatTool("who_said_that", "TOOLRESULT-ALICE");
        var chat = new FakeChatStreamClient
        {
            ChunkRounds =
            [
                [new ChatStreamDelta(null, [new ToolCallFragment(0, "c1", "who_said_that", "{\"phrase\":\"budget\"}")], "tool_calls")],
                [new ChatStreamDelta("Alice said it.", null, null)],
            ],
        };

        var events = await Run(chat, [stub]);

        Assert.Equal(2, chat.Calls);
        Assert.Equal(1, stub.Calls);
        Assert.Contains("budget", stub.LastArgs);

        Assert.Collection(events,
            e => Assert.Equal("who_said_that", Assert.IsType<ChatToolStartEvent>(e).Name),
            e => Assert.Equal("who_said_that", Assert.IsType<ChatToolEndEvent>(e).Name),
            e => Assert.Equal("Alice said it.", Assert.IsType<ChatTokenEvent>(e).Value));

        // The second model call carries the tool result back as a tool message.
        Assert.Contains(chat.ChunkCallMessages[1],
            m => System.Text.Json.JsonSerializer.Serialize(m).Contains("TOOLRESULT-ALICE"));
    }

    [Fact]
    public async Task UnknownTool_ReturnsErrorToModel_NoThrow()
    {
        var chat = new FakeChatStreamClient
        {
            ChunkRounds =
            [
                [new ChatStreamDelta(null, [new ToolCallFragment(0, "c1", "nope", "{}")], "tool_calls")],
                [new ChatStreamDelta("done", null, null)],
            ],
        };

        var events = await Run(chat, [new StubChatTool("who_said_that")]);

        Assert.Contains(events, e => e is ChatTokenEvent t && t.Value == "done");
        Assert.Contains(chat.ChunkCallMessages[1],
            m => System.Text.Json.JsonSerializer.Serialize(m).Contains("Unknown tool"));
    }

    [Fact]
    public void ExtractRecordingRefs_PullsNameAndWholeRecordingHref()
    {
        var rid = Guid.NewGuid();
        var result =
            $"1. When: … · Who: Alice · What: \"hi\" · Link: [Budget Review @ 04:12](/recordings/{rid}?t=252000)\n" +
            $"2. When: … · Who: Bob · What: \"yo\" · Link: [Budget Review @ 06:00](/recordings/{rid}?t=360000)";

        var refs = ChatToolOrchestrator.ExtractRecordingRefs(result);

        Assert.Contains(refs, r => r.Name == "Budget Review" && r.Href == $"/recordings/{rid}");
    }

    [Fact]
    public async Task ToolRun_EmitsRefEvents_ForReferencedRecordings()
    {
        var rid = Guid.NewGuid();
        var stub = new StubChatTool("who_said_that",
            $"1. When: x · Who: Alice · What: \"hi\" · Link: [Acme Sync @ 01:00](/recordings/{rid}?t=60000)");
        var chat = new FakeChatStreamClient
        {
            ChunkRounds =
            [
                [new ChatStreamDelta(null, [new ToolCallFragment(0, "c1", "who_said_that", "{}")], "tool_calls")],
                [new ChatStreamDelta("Alice mentioned it.", null, null)],
            ],
        };

        var events = await Run(chat, [stub]);

        var refEvt = Assert.Single(events.OfType<ChatRefEvent>());
        Assert.Equal("Acme Sync", refEvt.Name);
        Assert.Equal($"/recordings/{rid}", refEvt.Href);
    }

    [Fact]
    public async Task LoopIsBounded_ByMaxToolRounds()
    {
        // The model "always" wants to call a tool; the orchestrator must still terminate.
        var chat = new FakeChatStreamClient
        {
            ChunkRounds =
            [
                [new ChatStreamDelta(null, [new ToolCallFragment(0, "c", "who_said_that", "{}")], "tool_calls")],
            ],
        };

        await Run(chat, [new StubChatTool("who_said_that")]);

        Assert.Equal(ChatToolOrchestrator.MaxToolRounds, chat.Calls);
        // The final round offers no tools (forces a text answer in a real model).
        Assert.Null(chat.ChunkCallTools[^1]);
    }
}
