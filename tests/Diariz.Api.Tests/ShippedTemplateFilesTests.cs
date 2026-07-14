using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The regression bar for moving the built-in templates out of C# and into markdown files: the file-authored
/// standards must produce <b>exactly</b> the content the hand-built ones did, or every user's minutes change shape.
///
/// <para>These run against the <b>real shipped files</b> (the API project copies <c>meeting-types/*.md</c> into the
/// test output), so a mangled prompt or a malformed file fails here rather than in production.</para></summary>
public class ShippedTemplateFilesTests
{
    private static StandardMeetingType Std(string key) =>
        Standards.All.Single(s => s.Key == key);

    private static TemplateContent Content(string key) => TemplateContent.Parse(Std(key).ContentJson);

    [Fact]
    public void All_eight_standards_ship_and_parse()
    {
        Assert.Equal(8, Standards.All.Count);
        Assert.All(Standards.All, s => Assert.True(Content(s.Key).Validate().Ok, s.Key));
        Assert.All(Standards.All, s => Assert.NotEmpty(Content(s.Key).Sections));
    }

    [Fact]
    public void Every_standard_declares_the_context_a_minutes_template_needs()
    {
        Assert.All(Standards.All, s => Assert.Equal(
            FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Actions, s.Context));
    }

    // Every standard ends with the canonical actions table (the deterministic one, never LLM-invented).
    [Fact]
    public void Every_standard_renders_the_canonical_action_items()
    {
        Assert.All(Standards.All, s => Assert.True(Content(s.Key).HasField("action_items"), s.Key));
    }

    // The General default reproduces the original minutes structure. This is the exact shape the C# DSL built -
    // section titles, order, and block kinds - so the file-authored version is provably a like-for-like move.
    [Fact]
    public void The_General_default_has_the_same_shape_the_hand_built_one_did()
    {
        var content = Content(MeetingType.GeneralKey);

        Assert.Equal(
            ["Meeting details", "Purpose", "Discussion", "Decisions", "Open questions", "Next steps",
             "Enhanced notes", "Action items"],
            content.Sections.Select(s => s.Title));
        Assert.All(content.Sections, s => Assert.Equal(1, s.Level));

        // The details section is "Date: " + {{date}} + "Time: " + {{time}} + ... - literal text glued to a field,
        // which is what makes "Date: 2026-07-06" render on one line.
        var details = content.Sections[0].Blocks;
        Assert.Equal(
            [TemplateBlock.Boilerplate, TemplateBlock.FieldKind, TemplateBlock.Boilerplate, TemplateBlock.FieldKind,
             TemplateBlock.Boilerplate, TemplateBlock.FieldKind, TemplateBlock.Boilerplate, TemplateBlock.FieldKind],
            details.Select(b => b.Kind));
        Assert.Equal("Date: ", details[0].Text);
        Assert.Equal("date", details[1].Field);
        Assert.Equal(["date", "time", "attendees", "duration"],
            details.Where(b => b.Kind == TemplateBlock.FieldKind).Select(b => b.Field));

        Assert.Equal("State the purpose / context of the meeting in 1-2 lines.",
            content.Sections[1].Blocks.Single().Text);
        Assert.True(content.HasField("notes"));        // the Enhanced notes section
    }

    [Fact]
    public async Task The_General_default_composes_to_the_expected_document()
    {
        var content = Content(MeetingType.GeneralKey);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(
            content,
            name => name switch { "date" => "2026-07-06", "time" => "09:00", _ => null },
            block => Task.FromResult($"[{block.Text}]"));

        // The field glues to the text before it, so "Date: " + {{date}} lands on one line.
        Assert.Contains("# Meeting details\n\nDate: 2026-07-06\n\nTime: 09:00", md);
        Assert.Contains("# Purpose\n\n[State the purpose / context of the meeting in 1-2 lines.]", md);

        // A label whose field resolves to nothing is still emitted (the empty FIELD drops, the literal text does
        // not). That is unchanged from the hand-built template - the composer has always behaved this way - and it
        // is pinned here so moving to files is provably a like-for-like move, quirks included.
        Assert.Contains("Attendees: ", md);
        Assert.DoesNotContain("Enhanced notes", md);   // no notes resolved -> that section drops entirely
    }

    // A prompt must survive the round-trip through the file verbatim - it is the instruction the model is given.
    [Fact]
    public void Prompts_survive_the_file_verbatim()
    {
        var discussion = Content(MeetingType.GeneralKey).Sections.Single(s => s.Title == "Discussion");

        Assert.Equal(
            "Summarise the discussion grouped by theme (not chronologically), concise and decision-oriented. "
            + "Omit this section if there was no substantive discussion.",
            discussion.Blocks.Single().Text);
    }

    [Fact]
    public void The_emergency_template_is_usable_when_the_content_files_are_missing()
    {
        var emergency = MeetingTypeSeeder.EmergencyGeneral;

        Assert.NotEmpty(emergency.Sections);
        Assert.True(emergency.Validate().Ok);
        Assert.True(emergency.HasField("action_items"));
        Assert.NotEmpty(emergency.PromptBlocks());
    }
}
