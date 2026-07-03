using Diariz.Api.Mcp;

namespace Diariz.Api.Tests;

public class McpPromptsTests
{
    private static string? NoArgs(string _) => null;

    [Fact]
    public void Catalog_HasTheExpectedStarters()
    {
        var names = McpPrompts.All.Select(p => p.Name).ToList();
        Assert.Contains("summarise_last_meeting", names);
        Assert.Contains("open_action_items", names);
        Assert.Contains("find_discussion", names);
    }

    [Fact]
    public void FindDiscussion_DeclaresRequiredTopicArg()
    {
        var def = McpPrompts.Find("find_discussion");
        Assert.NotNull(def);
        var arg = Assert.Single(def!.Arguments);
        Assert.Equal("topic", arg.Name);
        Assert.True(arg.Required);
    }

    [Fact]
    public void Render_NoArgPrompts_ProduceInstructions()
    {
        Assert.Contains("most recent recording", McpPrompts.Render("summarise_last_meeting", NoArgs));
        Assert.Contains("action items", McpPrompts.Render("open_action_items", NoArgs)!);
    }

    [Fact]
    public void Render_FindDiscussion_InterpolatesTheTopic()
    {
        var text = McpPrompts.Render("find_discussion", key => key == "topic" ? "the Q3 budget" : null);
        Assert.Contains("\"the Q3 budget\"", text);
        Assert.Contains("search_transcripts", text!);
    }

    [Fact]
    public void Render_FindDiscussion_MissingTopic_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpPrompts.Render("find_discussion", NoArgs));
    }

    [Fact]
    public void Render_UnknownPrompt_ReturnsNull()
    {
        Assert.Null(McpPrompts.Render("nope", NoArgs));
        Assert.Null(McpPrompts.Find("nope"));
    }
}
