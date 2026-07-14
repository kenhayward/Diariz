using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The markdown authoring format for built-in templates: a document you can read in a PR diff, which
/// parses into the same <see cref="TemplateContent"/> the block editor produces.
///
/// <para>Headings become sections; <c>{{field}}</c> becomes a substituted recording value; <c>[[WRITE: …]]</c>
/// becomes a model prompt (the marker <c>SingleCallMinutesStrategy</c> already emits); <c>---</c> becomes a rule;
/// anything else is literal text.</para></summary>
public class TemplateMarkdownTests
{
    [Fact]
    public void A_heading_starts_a_section_at_its_level()
    {
        var content = TemplateMarkdown.Parse("# Purpose\nSome text.\n\n## Detail\nMore.");

        Assert.Equal(2, content.Sections.Count);
        Assert.Equal((1, "Purpose"), (content.Sections[0].Level, content.Sections[0].Title));
        Assert.Equal((2, "Detail"), (content.Sections[1].Level, content.Sections[1].Title));
    }

    // Content before any heading is a headless (level-0) section - the body alone, no heading invented for it.
    [Fact]
    public void Content_before_any_heading_is_a_headless_section()
    {
        var content = TemplateMarkdown.Parse("Just prose.\n\n# Later\nMore.");

        Assert.Equal(0, content.Sections[0].Level);
        Assert.Equal("Just prose.", content.Sections[0].Blocks[0].Text);
        Assert.Equal(1, content.Sections[1].Level);
    }

    [Fact]
    public void A_WRITE_marker_is_a_model_prompt()
    {
        var content = TemplateMarkdown.Parse("# Purpose\n[[WRITE: State the purpose in 1-2 lines.]]");

        var block = Assert.Single(content.Sections[0].Blocks);
        Assert.Equal(TemplateBlock.Prompt, block.Kind);
        Assert.Equal("State the purpose in 1-2 lines.", block.Text);
    }

    [Fact]
    public void A_WRITE_marker_may_span_lines()
    {
        var content = TemplateMarkdown.Parse("# P\n[[WRITE: Summarise the discussion,\ngrouped by theme.]]");

        var block = Assert.Single(content.Sections[0].Blocks);
        Assert.Equal(TemplateBlock.Prompt, block.Kind);
        Assert.Equal("Summarise the discussion, grouped by theme.", block.Text);
    }

    [Fact]
    public void A_field_on_its_own_is_a_field_block()
    {
        var content = TemplateMarkdown.Parse("# Notes\n{{notes}}");

        var block = Assert.Single(content.Sections[0].Blocks);
        Assert.Equal(TemplateBlock.FieldKind, block.Kind);
        Assert.Equal("notes", block.Field);
    }

    // "Date: {{date}}" is literal text plus a field, on one line - which is exactly what the seeded templates do,
    // and the composer's legacy rule glues a field to the text before it.
    [Fact]
    public void A_field_inline_with_text_splits_into_boilerplate_then_field()
    {
        var content = TemplateMarkdown.Parse("# Details\nDate: {{date}}");

        var blocks = content.Sections[0].Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.Equal(TemplateBlock.Boilerplate, blocks[0].Kind);
        Assert.Equal("Date: ", blocks[0].Text);
        Assert.Equal(TemplateBlock.FieldKind, blocks[1].Kind);
        Assert.Equal("date", blocks[1].Field);
    }

    [Fact]
    public void A_rule_becomes_an_hr_block()
    {
        var content = TemplateMarkdown.Parse("# S\nBefore.\n---\nAfter.");

        var kinds = content.Sections[0].Blocks.Select(b => b.Kind).ToList();
        Assert.Equal([TemplateBlock.Boilerplate, TemplateBlock.HorizontalLine, TemplateBlock.Boilerplate], kinds);
    }

    [Fact]
    public void Blank_lines_are_separators_not_blocks()
    {
        var content = TemplateMarkdown.Parse("# S\nOne.\n\n\nTwo.");

        Assert.Equal(2, content.Sections[0].Blocks.Count);
    }

    [Fact]
    public void No_break_after_is_emitted_so_the_composer_uses_its_legacy_rule()
    {
        var content = TemplateMarkdown.Parse("# Details\nDate: {{date}}");

        Assert.All(content.Sections[0].Blocks, b => Assert.Null(b.BreakAfter));
    }

    [Fact]
    public void Empty_input_is_empty_content()
    {
        Assert.Empty(TemplateMarkdown.Parse("").Sections);
        Assert.Empty(TemplateMarkdown.Parse("   \n\n ").Sections);
    }

    [Fact]
    public void The_result_is_valid_template_content()
    {
        var content = TemplateMarkdown.Parse("# Purpose\n[[WRITE: Do it.]]\n\n# Details\nDate: {{date}}");
        Assert.True(content.Validate().Ok);
    }

    // The whole point of the format: what you read in the file is what the model is asked to produce.
    [Fact]
    public void Round_trips_a_realistic_minutes_template()
    {
        const string md = """
            # Meeting details
            Date: {{date}}
            Attendees: {{attendees}}

            # Purpose
            [[WRITE: State the purpose of the meeting in 1-2 lines.]]

            # Action items
            {{action_items}}
            """;

        var content = TemplateMarkdown.Parse(md);

        Assert.Equal(3, content.Sections.Count);
        Assert.True(content.HasField("date"));
        Assert.True(content.HasField("attendees"));
        Assert.True(content.HasField("action_items"));
        Assert.Single(content.PromptBlocks());
    }
}
