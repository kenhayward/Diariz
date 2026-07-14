using System.Text;

namespace Diariz.Api.Services;

/// <summary>Assembles a meeting type's <see cref="TemplateContent"/> into the final Markdown minutes. Pure
/// except for the injected prompt resolver: headings come from sections, <c>boilerplate</c> is emitted verbatim,
/// <c>field</c> blocks are substituted via <paramref name="resolveField"/> (a field joins inline to a preceding
/// boilerplate/field, so "Date: " + date reads as one line), and each <c>prompt</c> block is replaced by the
/// Markdown returned by <paramref name="resolvePrompt"/> (called once per prompt block, in document order). A
/// section whose blocks all render empty is dropped entirely - never pad the minutes with empty headings.</summary>
public static class MeetingTypeMinutesComposer
{
    public static async Task<string> ComposeAsync(
        TemplateContent content,
        Func<string, string?> resolveField,
        Func<TemplateBlock, Task<string>> resolvePrompt)
    {
        var sections = new List<string>();
        foreach (var section in content.Sections ?? [])
        {
            var body = await RenderBlocksAsync(section.Blocks ?? [], resolveField, resolvePrompt);
            if (string.IsNullOrWhiteSpace(body)) continue; // omit an empty section (heading included)

            // Level 0 = a headless section: the body alone, no heading. It is what a bare prompt looks like as
            // a template - a formula's output has never carried a heading of its own, and must not start doing
            // so now that a formula IS a template. Any title on a level-0 section has nowhere to go, so it is
            // ignored rather than rejected.
            if (section.Level <= 0)
            {
                sections.Add(body);
                continue;
            }

            var heading = new string('#', Math.Clamp(section.Level, 1, 6)) + " " + section.Title.Trim();
            sections.Add($"{heading}\n\n{body}");
        }
        return string.Join("\n\n", sections).Trim();
    }

    /// <summary>Render one section's blocks into a body. Each block is rendered, empties are dropped, and adjacent
    /// blocks are joined by the separator chosen from the preceding block's <c>BreakAfter</c> (a null value falls
    /// back to the legacy rule: glue only before a field).</summary>
    private static async Task<string> RenderBlocksAsync(
        IReadOnlyList<TemplateBlock> blocks,
        Func<string, string?> resolveField,
        Func<TemplateBlock, Task<string>> resolvePrompt)
    {
        var rendered = new List<(TemplateBlock Block, string Text)>();
        foreach (var block in blocks)
        {
            var text = block.Kind switch
            {
                TemplateBlock.Boilerplate => block.Text ?? "",
                TemplateBlock.FieldKind => resolveField(block.Field ?? "") ?? "",
                TemplateBlock.Prompt => (await resolvePrompt(block) ?? "").Trim(),
                TemplateBlock.HorizontalLine => "---",
                _ => "",
            };
            rendered.Add((block, text));
        }

        var kept = rendered.Where(r => r.Text.Trim().Length > 0).ToList();
        if (kept.Count == 0) return "";

        var sb = new StringBuilder(kept[0].Text);
        for (var i = 1; i < kept.Count; i++)
        {
            sb.Append(Separator(kept[i - 1].Block, kept[i].Block));
            sb.Append(kept[i].Text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The whitespace between a rendered block and the next one. Honors the preceding block's explicit
    /// <c>BreakAfter</c>; a null value falls back to the legacy rule (glue only when the next block is a field). A
    /// horizontal rule on either side always forces a paragraph gap, so "text\n---" can't be read as a setext H2.</summary>
    private static string Separator(TemplateBlock prev, TemplateBlock next)
    {
        if (prev.Kind == TemplateBlock.HorizontalLine || next.Kind == TemplateBlock.HorizontalLine)
            return "\n\n";
        return (prev.BreakAfter ?? LegacyBreak(next)) switch
        {
            TemplateBlock.BreakNone => "",
            TemplateBlock.BreakLine => "\n",
            _ => "\n\n", // BreakParagraph (and any unexpected value) => paragraph gap
        };
    }

    private static string LegacyBreak(TemplateBlock next) =>
        next.Kind == TemplateBlock.FieldKind ? TemplateBlock.BreakNone : TemplateBlock.BreakParagraph;
}
