using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SummarizationPromptTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "We ship Friday."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 1000, 2000, "I'll write the tests."),
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
        var msgs = SummarizationPrompt.BuildMessages(SummarizationPrompt.DefaultTemplate, Segments, needName: false);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Equal("user", msgs[1].Role);
        Assert.Contains("Alice: We ship Friday.", msgs[1].Content);
        Assert.Contains("Bob: I'll write the tests.", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_RequestsPlainText_NotJson()
    {
        // The prompt must not demand strict/structured JSON (local models mangle it).
        var system = SummarizationPrompt.BuildMessages(SummarizationPrompt.DefaultTemplate, Segments, needName: false)[0].Content;
        Assert.Contains("plain text", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{output_shape}", system); // placeholder was substituted
        Assert.DoesNotContain("minified JSON", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMessages_AsksForTitle_OnlyWhenNeeded()
    {
        var withName = SummarizationPrompt.BuildMessages(SummarizationPrompt.DefaultTemplate, Segments, needName: true)[0].Content;
        var noName = SummarizationPrompt.BuildMessages(SummarizationPrompt.DefaultTemplate, Segments, needName: false)[0].Content;
        Assert.Contains("title", withName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("title", noName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMessages_TruncatesTranscriptToCharBudget()
    {
        var user = SummarizationPrompt.BuildMessages(SummarizationPrompt.DefaultTemplate, Segments, needName: false, charBudget: 10)[1].Content;
        // "Transcript:\n" prefix + 10 chars of transcript.
        Assert.True(user.Length <= "Transcript:\n".Length + 10);
    }

    // ---- Plain-text responses (the expected path) ----

    [Fact]
    public void ParseResponse_PlainText_IsTheSummary_WhenNoNameNeeded()
    {
        var json = ChatResponse("The team agreed to ship on Friday and Bob will write the tests.");

        var result = SummarizationPrompt.ParseResponse(json, needName: false);

        Assert.Equal("The team agreed to ship on Friday and Bob will write the tests.", result.Summary);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ParseResponse_TitleFirstLine_ThenSummary_WhenNameNeeded()
    {
        var json = ChatResponse("Ship Friday\nThe team agreed to ship on Friday.");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("Ship Friday", result.Name);
        Assert.Equal("The team agreed to ship on Friday.", result.Summary);
    }

    [Fact]
    public void ParseResponse_CleansTitleLine_LabelsAndQuotes()
    {
        var json = ChatResponse("Title: \"Ship Friday\"\n\nThe team agreed to ship on Friday.");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("Ship Friday", result.Name);
        Assert.Equal("The team agreed to ship on Friday.", result.Summary);
    }

    [Fact]
    public void ParseResponse_SingleLine_IsSummaryNotTitle_WhenNameNeeded()
    {
        // Only one line: treat it as the summary (the model skipped the title), not as a bare title.
        var json = ChatResponse("The team agreed to ship on Friday.");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("The team agreed to ship on Friday.", result.Summary);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ParseResponse_StripsCodeFence_PlainText()
    {
        var json = ChatResponse("```\nAll good.\n```");

        var result = SummarizationPrompt.ParseResponse(json, needName: false);

        Assert.Equal("All good.", result.Summary);
    }

    [Fact]
    public void ParseResponse_StripsModelTokens_InPlainText()
    {
        var json = ChatResponse("<|channel|>final<|message|>Just prose, no JSON here.");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.DoesNotContain("<|", result.Summary);
        Assert.Contains("Just prose", result.Summary);
    }

    // ---- Defensive: still parse a JSON object if a model emits one despite the plain-text ask ----

    [Fact]
    public void ParseResponse_StillExtractsJson_WhenModelEmitsIt()
    {
        var json = ChatResponse("{\"summary\":\"All good.\",\"name\":\"Ship Friday\"}");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("All good.", result.Summary);
        Assert.Equal("Ship Friday", result.Name);
    }

    [Fact]
    public void ParseResponse_IgnoresJsonName_WhenNotNeeded()
    {
        var json = ChatResponse("{\"summary\":\"All good.\",\"name\":\"Ship Friday\"}");

        var result = SummarizationPrompt.ParseResponse(json, needName: false);

        Assert.Equal("All good.", result.Summary);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ParseResponse_ExtractsJson_WrappedInModelTokens()
    {
        // gpt-oss "harmony" channel markers around the JSON object.
        var json = ChatResponse("<|channel|>final <|constrain|>{\"summary\":\"All good.\",\"name\":\"Sync\"}");

        var result = SummarizationPrompt.ParseResponse(json, needName: true);

        Assert.Equal("All good.", result.Summary);
        Assert.Equal("Sync", result.Name);
    }
}
