using System.Text;
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>The markdown authoring format for built-in templates - the one place prompts are written.
///
/// <para>A template used to be hand-built in C# (<c>Sec(1, "Purpose", Prompt("..."))</c>), which meant the words the
/// model is given were buried in code and unreviewable in a diff. They are now markdown files, and this parses one
/// into the same <see cref="TemplateContent"/> the block editor produces - so a file, an edited template, and a
/// user-authored formula are all the same thing.</para>
///
/// <code>
/// # Meeting details
/// Date: {{date}}
///
/// # Purpose
/// [[WRITE: State the purpose of the meeting in 1-2 lines.]]
/// </code>
///
/// <list type="bullet">
/// <item><c>#</c>/<c>##</c>/<c>###</c> - a section at that level.</item>
/// <item>Content before any heading - a <b>headless</b> (level-0) section: the body alone, no heading invented.</item>
/// <item><c>{{field}}</c> - a substituted recording value. Inline with text (<c>Date: {{date}}</c>) it splits into
/// literal text + the field, which the composer's legacy rule then glues back onto one line.</item>
/// <item><c>[[WRITE: instruction]]</c> - a model prompt. It may span lines. (The same marker
/// <see cref="SingleCallMinutesStrategy"/> emits, so the format and the prompt it produces read alike.)</item>
/// <item><c>---</c> on its own line - a horizontal rule.</item>
/// <item>Blank lines are separators, not content.</item>
/// </list>
///
/// <para>No <c>breakAfter</c> is emitted, so the composer applies its legacy rule - which is exactly what the
/// hand-built templates relied on, so their output is unchanged.</para></summary>
public static class TemplateMarkdown
{
    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Field = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled);

    public static TemplateContent Parse(string? markdown)
    {
        var text = (markdown ?? string.Empty).Replace("\r\n", "\n").Trim('﻿', ' ', '\n', '\t');
        if (text.Length == 0) return TemplateContent.Empty;

        var sections = new List<TemplateSection>();
        var level = 0;                       // 0 until the first heading: a headless preamble section.
        var title = string.Empty;
        var blocks = new List<TemplateBlock>();

        void Flush()
        {
            if (blocks.Count > 0) sections.Add(new TemplateSection(level, title, blocks.ToList()));
            blocks.Clear();
        }

        foreach (var line in JoinMarkers(text.Split('\n')))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;   // blank lines separate; the composer decides the whitespace.

            if (Heading.Match(trimmed) is { Success: true } h)
            {
                Flush();
                level = h.Groups[1].Value.Length;
                title = h.Groups[2].Value.Trim();
                continue;
            }

            if (trimmed == "---")
            {
                blocks.Add(new TemplateBlock(TemplateBlock.HorizontalLine));
                continue;
            }

            if (trimmed.StartsWith("[[WRITE:", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("]]"))
            {
                var instruction = trimmed["[[WRITE:".Length..^2].Trim();
                blocks.Add(new TemplateBlock(TemplateBlock.Prompt, Text: instruction));
                continue;
            }

            blocks.AddRange(SplitFields(trimmed));
        }

        Flush();
        return new TemplateContent(sections);
    }

    /// <summary>Fold a <c>[[WRITE: …]]</c> that spans several lines back into one, so the line loop can treat every
    /// marker as a single unit. The instruction's own line breaks become spaces - it is an instruction to a model,
    /// not laid-out text.</summary>
    private static IEnumerable<string> JoinMarkers(IEnumerable<string> lines)
    {
        var open = new StringBuilder();

        foreach (var line in lines)
        {
            if (open.Length > 0)
            {
                open.Append(' ').Append(line.Trim());
                if (line.TrimEnd().EndsWith("]]", StringComparison.Ordinal))
                {
                    yield return open.ToString();
                    open.Clear();
                }
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("[[WRITE:", StringComparison.OrdinalIgnoreCase) && !trimmed.EndsWith("]]"))
            {
                open.Append(trimmed);
                continue;
            }

            yield return line;
        }

        if (open.Length > 0) yield return open.ToString(); // unterminated marker - emit what we have.
    }

    /// <summary>Split one line into literal text and <c>{{field}}</c> blocks, in order.</summary>
    private static IEnumerable<TemplateBlock> SplitFields(string line)
    {
        var last = 0;
        foreach (Match m in Field.Matches(line))
        {
            if (m.Index > last)
                yield return new TemplateBlock(TemplateBlock.Boilerplate, Text: line[last..m.Index]);

            yield return new TemplateBlock(TemplateBlock.FieldKind, Field: m.Groups[1].Value);
            last = m.Index + m.Length;
        }

        if (last == 0)
        {
            yield return new TemplateBlock(TemplateBlock.Boilerplate, Text: line);
            yield break;
        }

        if (last < line.Length)
            yield return new TemplateBlock(TemplateBlock.Boilerplate, Text: line[last..]);
    }
}
