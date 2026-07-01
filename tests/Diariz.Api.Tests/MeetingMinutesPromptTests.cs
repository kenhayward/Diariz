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

    private const string Template =
        "Instructions here — no emojis.\n\n**Meeting Data**\nMeeting Date: {meeting_date}\n" +
        "Meeting Time: {meeting_time}\nTitle: {meeting_title}\nAttendees:{speaker_list}\n" +
        "Duration:{meeting_duration}\n\n## Transcript:";

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public void BuildMessages_SubstitutesPlaceholders_AndPutsTranscriptInAUserTurn()
    {
        var ctx = new MeetingMinutesContext(
            new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), "Weekly Sync",
            ["Alice", "Bob"], (60 + 5) * 60_000L); // 1h 05m

        var msgs = MeetingMinutesPrompt.BuildMessages(Template, ctx, Segments, 16000);

        Assert.Equal(2, msgs.Count);
        var system = msgs[0];
        Assert.Equal("system", system.Role);
        Assert.Contains("Meeting Date: 2026-03-04", system.Content);
        Assert.Contains("Meeting Time: 09:00", system.Content);
        Assert.Contains("Title: Weekly Sync", system.Content);
        Assert.Contains("Attendees: Alice, Bob", system.Content);
        Assert.Contains("Duration: 1h 05m", system.Content);
        Assert.DoesNotContain("{", system.Content); // every placeholder substituted

        var user = msgs[1];
        Assert.Equal("user", user.Role);
        Assert.Contains("Alice: Let's ship on Friday.", user.Content); // transcript is the (data) turn
        Assert.DoesNotContain("Instructions here", user.Content);      // instructions stay in the system turn
    }

    [Fact]
    public void BuildMessages_RendersSuppliedActionItems_IntoTheActionsPlaceholder()
    {
        var template = "Actions:\n{action_items}\n\n## Transcript:";
        var ctx = new MeetingMinutesContext(
            null, "T", [], null, [new ExtractedAction("Send the report", "Bob", "2026-03-06")]);

        var system = MeetingMinutesPrompt.BuildMessages(template, ctx, Segments, 16000)[0].Content;

        Assert.Contains("Send the report", system);
        Assert.Contains("Bob", system);
        Assert.Contains("2026-03-06", system);
        Assert.DoesNotContain("{action_items}", system);
    }

    [Fact]
    public void BuildMessages_ActionItemsPlaceholder_FallsBackWhenNoneSupplied()
    {
        var template = "Actions:\n{action_items}\n\n## Transcript:";
        var ctx = new MeetingMinutesContext(null, "T", [], null); // no actions
        var system = MeetingMinutesPrompt.BuildMessages(template, ctx, Segments, 16000)[0].Content;
        Assert.Contains("derive the action items from the transcript", system);
    }

    [Fact]
    public void BuildMessages_UsesPlaceholders_WhenMetadataMissing()
    {
        var ctx = new MeetingMinutesContext(null, "", [], null);
        var system = MeetingMinutesPrompt.BuildMessages(Template, ctx, Segments, 16000)[0].Content;
        Assert.Contains("Meeting Date: [placeholder]", system);
        Assert.Contains("Meeting Time: [placeholder]", system);
        Assert.Contains("Attendees: [placeholder]", system);
        Assert.Contains("Duration: [placeholder]", system);
    }

    [Fact]
    public void DefaultTemplate_HasTheMeetingDataPlaceholders_AndNoEmojiGuidance()
    {
        Assert.Contains("{meeting_date}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("{meeting_time}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("{action_items}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("{speaker_list}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("## Transcript:", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("emojis", MeetingMinutesPrompt.DefaultTemplate);
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
