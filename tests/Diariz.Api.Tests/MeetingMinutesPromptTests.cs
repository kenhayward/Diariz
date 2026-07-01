using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class MeetingMinutesPromptTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Let's ship on Friday."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 1000, 2000, "Agreed."),
    ];

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public void BuildMessages_System_IsProfessionalMarkdown_NoEmojis_NotJson()
    {
        var msgs = MeetingMinutesPrompt.BuildMessages(Segments, meetingDate: null, charBudget: 16000);

        var system = msgs[0];
        Assert.Equal("system", system.Role);
        Assert.Contains("meeting minutes", system.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Markdown", system.Content);
        Assert.Contains("Action Items", system.Content);
        Assert.Contains("emojis", system.Content); // instructs NOT to use them
        // Minutes are Markdown, not the summary's JSON contract.
        Assert.DoesNotContain("JSON", system.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMessages_User_CarriesTranscript_AndOptionalMeetingDate()
    {
        var withDate = MeetingMinutesPrompt.BuildMessages(
            Segments, new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), 16000);
        Assert.Contains("Meeting date: 2026-03-04", withDate[1].Content);
        Assert.Contains("Alice: Let's ship on Friday.", withDate[1].Content);

        var noDate = MeetingMinutesPrompt.BuildMessages(Segments, meetingDate: null, charBudget: 16000);
        Assert.DoesNotContain("Meeting date:", noDate[1].Content);
    }

    [Fact]
    public void CleanResponse_ReturnsMarkdown_StrippingFencesAndModelTokens()
    {
        var fenced = ChatResponse("```markdown\n# Meeting\n\n- point\n```");
        Assert.Equal("# Meeting\n\n- point", MeetingMinutesPrompt.CleanResponse(fenced));

        var tokened = ChatResponse("<|channel|>final<|message|># Notes");
        Assert.Contains("# Notes", MeetingMinutesPrompt.CleanResponse(tokened));
        Assert.DoesNotContain("<|", MeetingMinutesPrompt.CleanResponse(tokened));
    }
}
