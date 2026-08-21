using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain.Entities;

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
            Guid.NewGuid(), new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), "Weekly Sync",
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
    public void BuildMessages_LeavesTheMultipleSpeakersSlotOffTheAttendeesLine()
    {
        // Overlapping speech is not a person who attended, so the model is not told it was one.
        var ctx = new MeetingMinutesContext(
            Guid.NewGuid(), null, "Weekly Sync", ["Alice", Speaker.MultiSpeakerName, "Bob"], null);

        var system = MeetingMinutesPrompt.BuildMessages(Template, ctx, Segments, 16000)[0].Content;

        Assert.Contains("Attendees: Alice, Bob", system);
        Assert.DoesNotContain(Speaker.MultiSpeakerName, system);
    }

    [Fact]
    public void BuildMessages_AttendeesFallsBackToThePlaceholder_WhenOnlyOverlappingSpeech()
    {
        var ctx = new MeetingMinutesContext(
            Guid.NewGuid(), null, "Weekly Sync", [Speaker.MultiSpeakerName], null);

        var system = MeetingMinutesPrompt.BuildMessages(Template, ctx, Segments, 16000)[0].Content;

        Assert.Contains("Attendees: [placeholder]", system);
    }

    [Fact]
    public void BuildMessages_StillLabelsOverlappingSegmentsInTheTranscript()
    {
        // The roster drops it; the transcript must not - the model still needs to see who said what.
        var segments = new List<SegmentDto>(Segments)
        {
            new(Guid.NewGuid(), "SPEAKER_02", Speaker.MultiSpeakerName, 2000, 3000, "Both at once."),
        };
        var ctx = new MeetingMinutesContext(Guid.NewGuid(), null, "Weekly Sync", ["Alice"], null);

        var user = MeetingMinutesPrompt.BuildMessages(Template, ctx, segments, 16000)[1].Content;

        Assert.Contains(Speaker.MultiSpeakerName + ": Both at once.", user);
    }

    [Fact]
    public void RenderActionItems_BuildsATableFromTheCanonicalActions()
    {
        var md = MeetingMinutesPrompt.RenderActionItems(
        [
            new ExtractedAction("Send the report", "Bob", "2026-03-06"),
            new ExtractedAction("Book the room", "", ""), // blank owner/due render as empty cells
        ]);

        Assert.Contains("## Action Items", md);
        Assert.Contains("| Action | Owner | Due date |", md);
        Assert.Contains("| Send the report | Bob | 2026-03-06 |", md);
        Assert.Contains("| Book the room |  |  |", md);
    }

    // The table alone, with no "## Action Items" heading: this is what the `action_items` merge field substitutes,
    // where the template author has already written their own heading. RenderActionItems (above) keeps the heading
    // because it appends a SECTION to model-written minutes, which have nothing to hang the table under.
    [Fact]
    public void ActionItemsTable_IsTheTableWithoutAHeading()
    {
        var md = MeetingMinutesPrompt.ActionItemsTable([new ExtractedAction("Send the report", "Bob", "2026-03-06")]);

        Assert.DoesNotContain("#", md);
        Assert.StartsWith("| Action | Owner | Due date |", md);
        Assert.Contains("| Send the report | Bob | 2026-03-06 |", md);
        Assert.Equal("", MeetingMinutesPrompt.ActionItemsTable(null));
        Assert.Equal("", MeetingMinutesPrompt.ActionItemsTable([]));
    }

    [Fact]
    public void RenderActionItems_IsTheHeadingPlusThatSameTable()
    {
        IReadOnlyList<ExtractedAction> actions = [new ExtractedAction("Send the report", "Bob", "2026-03-06")];
        Assert.Equal(
            "## Action Items\n\n" + MeetingMinutesPrompt.ActionItemsTable(actions),
            MeetingMinutesPrompt.RenderActionItems(actions));
    }

    [Fact]
    public void RenderActionItems_EscapesPipes_SkipsBlankRows_EmptyWhenNone()
    {
        var md = MeetingMinutesPrompt.RenderActionItems(
        [
            new ExtractedAction("A | B split", "Al", ""),
            new ExtractedAction("   ", "x", "y"), // blank action text → skipped
        ]);
        Assert.Contains("A \\| B split", md);   // pipe escaped so the table stays intact
        Assert.DoesNotContain("| x |", md);     // the blank-text row is dropped

        Assert.Equal("", MeetingMinutesPrompt.RenderActionItems(null));
        Assert.Equal("", MeetingMinutesPrompt.RenderActionItems([]));
    }

    [Fact]
    public void WithActionItems_AppendsTheSection_OrLeavesMinutesUnchangedWhenNoActions()
    {
        Assert.Equal("# Minutes", MeetingMinutesPrompt.WithActionItems("# Minutes", null));

        var appended = MeetingMinutesPrompt.WithActionItems("# Minutes", [new ExtractedAction("Do X", "", "")]);
        Assert.StartsWith("# Minutes", appended);
        Assert.Contains("## Action Items", appended);
        Assert.Contains("Do X", appended);
    }

    [Fact]
    public void BuildMessages_UsesPlaceholders_WhenMetadataMissing()
    {
        var ctx = new MeetingMinutesContext(Guid.NewGuid(), null, "", [], null);
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
        Assert.Contains("{speaker_list}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("## Transcript:", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("emojis", MeetingMinutesPrompt.DefaultTemplate);
        // Actions are appended deterministically — no placeholder, and the model is told not to produce them.
        Assert.DoesNotContain("{action_items}", MeetingMinutesPrompt.DefaultTemplate);
        Assert.Contains("Do NOT produce an \"Action Items\"", MeetingMinutesPrompt.DefaultTemplate);
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
