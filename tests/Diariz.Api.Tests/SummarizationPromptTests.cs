using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SummarizationPromptTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto("SPEAKER_00", "Alice", 0, 1000, "We ship Friday."),
        new SegmentDto("SPEAKER_01", "Bob", 1000, 2000, "I'll write the tests."),
    ];

    // Wraps model output the way an OpenAI chat-completions response does.
    private static string ChatResponse(string content)
    {
        var obj = new { choices = new[] { new { message = new { role = "assistant", content } } } };
        return JsonSerializer.Serialize(obj);
    }

    [Fact]
    public void BuildMessages_HasSystemAndUser_WithTranscript()
    {
        var msgs = SummarizationPrompt.BuildMessages(Segments, needName: false);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Equal("user", msgs[1].Role);
        Assert.Contains("Alice: We ship Friday.", msgs[1].Content);
        Assert.Contains("Bob: I'll write the tests.", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_AsksForName_OnlyWhenNeeded()
    {
        Assert.Contains("\"name\"", SummarizationPrompt.BuildMessages(Segments, needName: true)[0].Content);
        Assert.DoesNotContain("\"name\"", SummarizationPrompt.BuildMessages(Segments, needName: false)[0].Content);
    }

    [Fact]
    public void BuildMessages_TruncatesTranscriptToCharBudget()
    {
        var user = SummarizationPrompt.BuildMessages(Segments, needName: false, charBudget: 10)[1].Content;
        // "Transcript:\n" prefix + 10 chars of transcript.
        Assert.True(user.Length <= "Transcript:\n".Length + 10);
    }

    [Fact]
    public void ParseResponse_ExtractsSummaryAndName()
    {
        var json = ChatResponse("{\"summary\":\"All good.\",\"name\":\"Ship Friday\"}");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("All good.", result.Summary);
        Assert.Equal("Ship Friday", result.Name);
    }

    [Fact]
    public void ParseResponse_IgnoresName_WhenNotNeeded()
    {
        var json = ChatResponse("{\"summary\":\"All good.\",\"name\":\"Ship Friday\"}");

        var result = SummarizationPrompt.ParseResponse(json, needName: false);

        Assert.Equal("All good.", result.Summary);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ParseResponse_StripsCodeFence()
    {
        var json = ChatResponse("```json\n{\"summary\":\"Fenced.\"}\n```");

        var result = SummarizationPrompt.ParseResponse(json, needName: false);

        Assert.Equal("Fenced.", result.Summary);
    }

    [Fact]
    public void ParseResponse_FallsBackToRawContent_OnMalformedJson()
    {
        var json = ChatResponse("Just a plain sentence, not JSON.");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("Just a plain sentence, not JSON.", result.Summary);
        Assert.Null(result.Name);
    }
}
