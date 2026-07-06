using System.Text;

namespace Diariz.Api.Services;

/// <summary>Assembles a meeting type's <see cref="MeetingTypeContent"/> into the final Markdown minutes. Pure
/// except for the injected prompt resolver: headings come from sections, <c>boilerplate</c> is emitted verbatim,
/// <c>field</c> blocks are substituted via <paramref name="resolveField"/> (a field joins inline to a preceding
/// boilerplate/field, so "Date: " + date reads as one line), and each <c>prompt</c> block is replaced by the
/// Markdown returned by <paramref name="resolvePrompt"/> (called once per prompt block, in document order). A
/// section whose blocks all render empty is dropped entirely - never pad the minutes with empty headings.</summary>
public static class MeetingTypeMinutesComposer
{
    public static async Task<string> ComposeAsync(
        MeetingTypeContent content,
        Func<string, string?> resolveField,
        Func<TemplateBlock, Task<string>> resolvePrompt)
    {
        var sections = new List<string>();
        foreach (var section in content.Sections ?? [])
        {
            var body = await RenderBlocksAsync(section.Blocks ?? [], resolveField, resolvePrompt);
            if (string.IsNullOrWhiteSpace(body)) continue; // omit an empty section (heading included)

            var heading = new string('#', Math.Clamp(section.Level, 1, 6)) + " " + section.Title.Trim();
            sections.Add($"{heading}\n\n{body}");
        }
        return string.Join("\n\n", sections).Trim();
    }

    /// <summary>Render one section's blocks into a body. Consecutive inline blocks (boilerplate/field) build one
    /// line; a prompt block is its own paragraph. Chunks are blank-line separated.</summary>
    private static async Task<string> RenderBlocksAsync(
        IReadOnlyList<TemplateBlock> blocks,
        Func<string, string?> resolveField,
        Func<TemplateBlock, Task<string>> resolvePrompt)
    {
        var chunks = new List<string>();
        var open = new StringBuilder();
        var hasOpen = false;

        void Flush()
        {
            if (hasOpen)
            {
                if (open.ToString().Trim().Length > 0) chunks.Add(open.ToString());
                open.Clear();
                hasOpen = false;
            }
        }

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case TemplateBlock.Boilerplate:
                    Flush();
                    open.Append(block.Text ?? "");
                    hasOpen = true;
                    break;
                case TemplateBlock.FieldKind:
                    open.Append(resolveField(block.Field ?? "") ?? "");
                    hasOpen = true; // stays open so a following boilerplate flushes it
                    break;
                case TemplateBlock.Prompt:
                    Flush();
                    var md = (await resolvePrompt(block) ?? "").Trim();
                    if (md.Length > 0) chunks.Add(md);
                    break;
            }
        }
        Flush();

        return string.Join("\n\n", chunks.Where(c => c.Trim().Length > 0)).Trim();
    }
}
