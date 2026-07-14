using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Pure (de)serialisation + validation of a meeting-type's structured minutes template: an ordered list
/// of H1/H2 sections whose blocks are boilerplate text, a substituted recording field, or a model prompt.</summary>
public class TemplateContentTests
{
    private static TemplateContent Sample() => new(
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
        var parsed = TemplateContent.Parse(content.Serialize());

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
        var parsed = TemplateContent.Parse(json);
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
        var content = new TemplateContent([new TemplateSection(4, "Bad", [])]);
        var (ok, error) = content.Validate();
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_accepts_an_H3_heading()
    {
        var content = new TemplateContent(
            [new TemplateSection(3, "Sub-sub", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "x")])]);
        var (ok, error) = content.Validate();
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_accepts_a_horizontal_line_block()
    {
        // A horizontal-line block carries neither text nor a field - it just emits a rule.
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.HorizontalLine)])]);
        var (ok, error) = content.Validate();
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_rejects_an_empty_section_title()
    {
        var content = new TemplateContent([new TemplateSection(1, "  ", [])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_block_kind()
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock("bogus", Text: "x")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_substitution_field()
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.FieldKind, Field: "salary")])]);
        Assert.False(content.Validate().Ok);
    }

    [Theory]
    [InlineData(TemplateBlock.Boilerplate)]
    [InlineData(TemplateBlock.Prompt)]
    public void Validate_rejects_empty_text_for_boilerplate_and_prompt(string kind)
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock(kind, Text: "   ")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Fields_catalog_lists_the_supported_substitutions()
    {
        Assert.Contains("date", TemplateContent.Fields);
        Assert.Contains("attendees", TemplateContent.Fields);
        Assert.Contains("action_items", TemplateContent.Fields);
    }

    [Theory]
    [InlineData(TemplateBlock.BreakNone)]
    [InlineData(TemplateBlock.BreakLine)]
    [InlineData(TemplateBlock.BreakParagraph)]
    [InlineData(null)]
    public void Validate_accepts_a_known_or_null_break_after(string? breakAfter)
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: breakAfter)])]);
        Assert.True(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_an_unknown_break_after()
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: "double")])]);
        Assert.False(content.Validate().Ok);
    }

    // Level 0 = a headless section (body only, no heading) - the shape a bare prompt takes as a template.
    [Fact]
    public void Validate_accepts_a_level_zero_section()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise it.")])]);
        Assert.True(content.Validate().Ok);
    }

    // A headless section has nowhere to show a title, so it doesn't need one.
    [Fact]
    public void Validate_does_not_require_a_title_on_a_level_zero_section()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "   ", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        Assert.True(content.Validate().Ok);
    }

    [Fact]
    public void Validate_still_requires_a_title_on_a_headed_section()
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_still_rejects_a_level_beyond_three()
    {
        var content = new TemplateContent(
            [new TemplateSection(4, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        Assert.False(content.Validate().Ok);
    }

    [Fact]
    public void Validate_rejects_a_negative_level()
    {
        var content = new TemplateContent(
            [new TemplateSection(-1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        Assert.False(content.Validate().Ok);
    }

    // A formula used to be nothing but a prompt. FromPrompt/BarePrompt are the round-trip that lets one be
    // stored as a template without changing what it does - and BarePrompt is what the runner checks to decide
    // whether a template needs composing at all, or is just a question to ask.
    [Fact]
    public void FromPrompt_wraps_a_prompt_in_one_headless_section()
    {
        var content = TemplateContent.FromPrompt("Draft a follow-up.");

        var section = Assert.Single(content.Sections);
        Assert.Equal(0, section.Level);
        var block = Assert.Single(section.Blocks);
        Assert.Equal(TemplateBlock.Prompt, block.Kind);
        Assert.Equal("Draft a follow-up.", block.Text);
    }

    [Fact]
    public void FromPrompt_round_trips_through_BarePrompt()
    {
        Assert.Equal("Draft a follow-up.", TemplateContent.FromPrompt("Draft a follow-up.").BarePrompt());
    }

    [Fact]
    public void BarePrompt_is_null_once_the_template_has_a_heading()
    {
        var content = new TemplateContent(
            [new TemplateSection(1, "Summary", [new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise.")])]);
        Assert.Null(content.BarePrompt());
    }

    [Fact]
    public void BarePrompt_is_null_once_the_template_has_more_than_one_block()
    {
        var content = new TemplateContent(
        [
            new TemplateSection(0, "",
            [
                new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise."),
                new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
            ]),
        ]);
        Assert.Null(content.BarePrompt());
    }

    [Fact]
    public void BarePrompt_is_null_for_a_single_non_prompt_block()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        Assert.Null(content.BarePrompt());
    }

    [Fact]
    public void BarePrompt_is_null_for_more_than_one_section()
    {
        var content = new TemplateContent(
        [
            new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Prompt, Text: "A")]),
            new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Prompt, Text: "B")]),
        ]);
        Assert.Null(content.BarePrompt());
    }

    [Fact]
    public void BarePrompt_is_null_for_empty_content()
    {
        Assert.Null(TemplateContent.Empty.BarePrompt());
    }
}
