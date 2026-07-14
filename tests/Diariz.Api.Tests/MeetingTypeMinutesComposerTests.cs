using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Pure assembly of a meeting type's template into Markdown: headings from sections, boilerplate emitted
/// verbatim, fields substituted (a field joins inline to a preceding boilerplate, so "Date: " + date reads as one
/// line), and each model-prompt block replaced by the text the prompt resolver returns. Empty sections are dropped.</summary>
public class MeetingTypeMinutesComposerTests
{
    private static string Field(string name) => name switch
    {
        "date" => "2026-07-06",
        "attendees" => "Alice, Bob",
        _ => "",
    };

    private static Task<string> Prompt(TemplateBlock b) => Task.FromResult($"[answer to: {b.Text}]");

    [Fact]
    public async Task Composes_headings_boilerplate_fields_and_prompt_output_in_order()
    {
        var content = new TemplateContent(
        [
            new TemplateSection(1, "Details",
            [
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Date: "),
                new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
            ]),
            new TemplateSection(1, "Summary",
            [
                new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise it."),
            ]),
        ]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.Equal(
            "# Details\n\nDate: 2026-07-06\n\n# Summary\n\n[answer to: Summarise it.]",
            md);
    }

    [Fact]
    public async Task Renders_h2_for_level_2_sections()
    {
        var content = new TemplateContent(
            [new TemplateSection(2, "Sub", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi")])]);
        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);
        Assert.StartsWith("## Sub", md);
    }

    [Fact]
    public async Task Omits_a_section_whose_blocks_all_render_empty()
    {
        // The field resolves to "" and there is no other content, so the whole section (heading included) is dropped.
        var content = new TemplateContent(
        [
            new TemplateSection(1, "Empty", [new TemplateBlock(TemplateBlock.FieldKind, Field: "missing")]),
            new TemplateSection(1, "Kept", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "content")]),
        ]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.DoesNotContain("Empty", md);
        Assert.Contains("# Kept", md);
    }

    [Fact]
    public async Task Prompt_resolver_is_called_once_per_prompt_block_in_document_order()
    {
        var seen = new List<string>();
        Task<string> Track(TemplateBlock b) { seen.Add(b.Text!); return Task.FromResult("x"); }
        var content = new TemplateContent(
        [
            new TemplateSection(1, "A", [new TemplateBlock(TemplateBlock.Prompt, Text: "first")]),
            new TemplateSection(1, "B", [new TemplateBlock(TemplateBlock.Prompt, Text: "second")]),
        ]);

        await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Track);

        Assert.Equal(["first", "second"], seen);
    }

    [Theory]
    [InlineData(TemplateBlock.BreakNone, "AB")]
    [InlineData(TemplateBlock.BreakLine, "A\nB")]
    [InlineData(TemplateBlock.BreakParagraph, "A\n\nB")]
    public async Task Break_after_controls_the_gap_between_two_blocks(string breakAfter, string expectedBody)
    {
        var content = new TemplateContent(
        [
            new TemplateSection(1, "S",
            [
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "A", BreakAfter: breakAfter),
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "B"),
            ]),
        ]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.Equal($"# S\n\n{expectedBody}", md);
    }

    [Fact]
    public async Task Horizontal_line_renders_as_a_rule_isolated_by_blank_lines()
    {
        // Even when the preceding block asks for no break, the rule must sit on its own paragraph - otherwise
        // Markdown reads "Intro\n---" as a setext H2 heading rather than a horizontal rule.
        var content = new TemplateContent(
        [
            new TemplateSection(1, "S",
            [
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Intro", BreakAfter: TemplateBlock.BreakNone),
                new TemplateBlock(TemplateBlock.HorizontalLine),
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Outro"),
            ]),
        ]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.Equal("# S\n\nIntro\n\n---\n\nOutro", md);
    }

    [Fact]
    public async Task Null_break_after_falls_back_to_legacy_field_glue()
    {
        // No BreakAfter set anywhere: a field still glues to the preceding boilerplate, two boilerplates break.
        var content = new TemplateContent(
        [
            new TemplateSection(1, "S",
            [
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Date: "),
                new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Next line"),
            ]),
        ]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.Equal("# S\n\nDate: 2026-07-06\n\nNext line", md);
    }

    // A level-0 section emits its body with NO heading. This is what lets a formula - which today is a bare
    // prompt whose output carries no heading of its own - be expressed as a template without silently gaining
    // an "# H1" at the top of every document it has ever produced.
    [Fact]
    public async Task A_level_zero_section_emits_no_heading()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise it.")])]);

        var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

        Assert.Equal("[answer to: Summarise it.]", md);
    }

    [Fact]
    public async Task A_level_zero_section_ignores_any_title_it_carries()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "Ignored", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "body")])]);

        Assert.Equal("body", await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt));
    }

    [Fact]
    public async Task A_level_zero_section_still_drops_out_when_it_renders_empty()
    {
        var content = new TemplateContent(
            [new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.FieldKind, Field: "unknown")])]);

        Assert.Equal("", await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt));
    }

    [Fact]
    public async Task A_level_zero_section_sits_alongside_headed_ones()
    {
        var content = new TemplateContent(
        [
            new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "Preamble.")]),
            new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "body")]),
        ]);

        Assert.Equal("Preamble.\n\n# S\n\nbody", await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt));
    }
}
