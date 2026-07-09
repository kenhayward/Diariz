using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Pure (de)serialisation + validation of a meeting-type's structured minutes template: an ordered list
/// of H1/H2 sections whose blocks are boilerplate text, a substituted recording field, or a model prompt.</summary>
public class MeetingTypeContentTests
{
    private static MeetingTypeContent Sample() => new(
    [
        new TemplateSection(1, "Overview",
        [
            new TemplateBlock(TemplateBlock.Boilerplate, Text: "Meeting date: "),
            new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
            new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise the meeting in one paragraph."),
        ]),
        new TemplateSection(2, "Action items",
        [
            new TemplateBlock(TemplateBlock.FieldKind, Field: "action_items"),
        ]),
    ]);

    [Fact]
    public void Serialize_then_Parse_round_trips()
    {
        var content = Sample();
        var parsed = MeetingTypeContent.Parse(content.Serialize());

        Assert.Equal(2, parsed.Sections.Count);
        Assert.Equal("Overview", parsed.Sections[0].Title);
        Assert.Equal(1, parsed.Sections[0].Level);
        Assert.Equal(3, parsed.Sections[0].Blocks.Count);
        Assert.Equal(TemplateBlock.Prompt, parsed.Sections[0].Blocks[2].Kind);
        Assert.Equal("Summarise the meeting in one paragraph.", parsed.Sections[0].Blocks[2].Text);
        Assert.Equal("action_items", parsed.Sections[1].Blocks[0].Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void Parse_of_missing_or_garbage_yields_empty_content(string? json)
    {
        var parsed = MeetingTypeContent.Parse(json);
        Assert.Empty(parsed.Sections);
    }

    [Fact]
    public void Validate_accepts_a_well_formed_template()
    {
        var (ok, error) = Sample().Validate();
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_rejects_a_heading_level_outside_1_to_3()
    {
        var content = new MeetingTypeContent([new TemplateSection(4, "Bad", [])]);
        var (ok, error) = content.Validate();
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_accepts_an_H3_heading()
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(3, "Sub-sub", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "x")])]);
        var (ok, error) = content.Validate();
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_accepts_a_horizontal_line_block()
    {
        // A horizontal-line block carries neither text nor a field - it just emits a rule.
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.HorizontalLine)])]);
        var (ok, error) = content.Validate();
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_rejects_an_empty_section_title()
    {
        var content = new MeetingTypeContent([new TemplateSection(1, "  ", [])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_block_kind()
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock("bogus", Text: "x")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_substitution_field()
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.FieldKind, Field: "salary")])]);
        Assert.False(content.Validate().Ok);
    }

    [Theory]
    [InlineData(TemplateBlock.Boilerplate)]
    [InlineData(TemplateBlock.Prompt)]
    public void Validate_rejects_empty_text_for_boilerplate_and_prompt(string kind)
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock(kind, Text: "   ")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Fields_catalog_lists_the_supported_substitutions()
    {
        Assert.Contains("date", MeetingTypeContent.Fields);
        Assert.Contains("attendees", MeetingTypeContent.Fields);
        Assert.Contains("action_items", MeetingTypeContent.Fields);
    }

    [Theory]
    [InlineData(TemplateBlock.BreakNone)]
    [InlineData(TemplateBlock.BreakLine)]
    [InlineData(TemplateBlock.BreakParagraph)]
    [InlineData(null)]
    public void Validate_accepts_a_known_or_null_break_after(string? breakAfter)
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: breakAfter)])]);
        Assert.True(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_break_after()
    {
        var content = new MeetingTypeContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: "double")])]);
        Assert.False(content.Validate().Ok);
    }
}
