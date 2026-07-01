using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ActionsPromptTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 3000, "Bob, please send the report by Friday."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 3000, 6000, "Will do."),
    ];

    private static string Chat(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    [Fact]
    public void BuildMessages_AsksForAJsonArrayOfActions_WithTheTranscript()
    {
        var msgs = ActionsPrompt.BuildMessages(
            ActionsPrompt.DefaultTemplate, Segments, new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("action", msgs[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", msgs[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Meeting date: 2026-03-04", msgs[0].Content); // {calendar_date} substituted
        Assert.DoesNotContain("{calendar_date}", msgs[0].Content);
        Assert.Contains("Alice: Bob, please send the report by Friday.", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_UsesUnknown_WhenNoMeetingDate()
    {
        var msgs = ActionsPrompt.BuildMessages(ActionsPrompt.DefaultTemplate, Segments, meetingDate: null);
        Assert.Contains("Meeting date: [unknown]", msgs[0].Content);
    }

    [Fact]
    public void ParseResponse_ReadsActions_WithActorAndDeadline()
    {
        var content = """[{"action":"Send the report","actor":"Bob","deadline":"Friday"}]""";
        var actions = ActionsPrompt.ParseResponse(Chat(content));

        var a = Assert.Single(actions);
        Assert.Equal("Send the report", a.Text);
        Assert.Equal("Bob", a.Actor);
        Assert.Equal("Friday", a.Deadline);
    }

    [Fact]
    public void ParseResponse_EmptyArray_ReturnsNoActions()
    {
        Assert.Empty(ActionsPrompt.ParseResponse(Chat("[]")));
    }

    [Fact]
    public void ParseResponse_MissingActorOrDeadline_DefaultsToEmpty()
    {
        var content = """[{"action":"Follow up"}]""";
        var a = Assert.Single(ActionsPrompt.ParseResponse(Chat(content)));

        Assert.Equal("Follow up", a.Text);
        Assert.Equal("", a.Actor);
        Assert.Equal("", a.Deadline);
    }

    [Fact]
    public void ParseResponse_ToleratesCodeFencesAndSurroundingProse()
    {
        var content = "Here are the actions:\n```json\n[{\"action\":\"Ship it\",\"actor\":\"\",\"deadline\":\"\"}]\n```";
        var a = Assert.Single(ActionsPrompt.ParseResponse(Chat(content)));

        Assert.Equal("Ship it", a.Text);
    }

    [Fact]
    public void ParseResponse_DropsEntriesWithNoActionText()
    {
        var content = """[{"action":"","actor":"x"},{"action":"Real one"}]""";
        var a = Assert.Single(ActionsPrompt.ParseResponse(Chat(content)));

        Assert.Equal("Real one", a.Text);
    }

    [Fact]
    public void ParseResponse_Garbage_ReturnsEmpty()
    {
        Assert.Empty(ActionsPrompt.ParseResponse(Chat("I could not find any actions.")));
    }
}
