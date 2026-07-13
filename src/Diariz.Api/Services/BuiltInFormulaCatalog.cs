using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>A Diariz-provided (built-in) formula loaded from a git-editable markdown file in formulas/.</summary>
public record BuiltInFormulaSpec(string Name, string? Description, string Prompt, FormulaContext Context);

/// <summary>Loads the Diariz-provided starter formulas from formulas/*.md, mirroring the editable prompts/
/// templates. Each file is a small key:value frontmatter block delimited by '---' lines, then the prompt as
/// the markdown body:
/// <code>
/// ---
/// name: Follow-up email
/// description: Draft a follow-up email.
/// context: Transcript, Summary, Actions
/// ---
/// (prompt body...)
/// </code>
/// <see cref="Parse"/> is pure (unit-tested); <see cref="LoadFrom"/> does the I/O and skips a
/// malformed/unreadable file rather than crashing boot.</summary>
public static class BuiltInFormulaCatalog
{
    // Transcript|Notes|Attachments|Summary|Minutes|Actions = 63; rejects unknown bits from a numeric context.
    private const int ValidContextMask =
        (int)(FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Attachments
            | FormulaContext.Summary | FormulaContext.Minutes | FormulaContext.Actions);

    /// <summary>Parse one formula markdown file. Throws <see cref="FormatException"/> on malformed input
    /// (missing/unterminated frontmatter, missing name, empty body, or an unknown context flag).</summary>
    public static BuiltInFormulaSpec Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n").TrimStart('﻿').TrimStart();
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new FormatException($"{source}: must start with a '---' frontmatter block.");

        var afterOpen = normalized[4..]; // past "---\n"
        var endIdx = afterOpen.IndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0)
            throw new FormatException($"{source}: frontmatter block is not closed with '---'.");

        var frontmatter = afterOpen[..endIdx];
        var body = afterOpen[(endIdx + 4)..].TrimStart('\n').Trim();
        if (body.Length == 0)
            throw new FormatException($"{source}: the prompt body (after the frontmatter) is empty.");

        string? name = null, description = null, contextRaw = null;
        foreach (var raw in frontmatter.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"{source}: frontmatter line '{line}' is not 'key: value'.");
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "context": contextRaw = value; break;
                // Unknown keys ignored for forward-compatibility.
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"{source}: missing required 'name'.");

        var context = FormulaContext.None;
        if (!string.IsNullOrWhiteSpace(contextRaw))
        {
            if (!Enum.TryParse(contextRaw, ignoreCase: true, out context)
                || ((int)context & ~ValidContextMask) != 0)
                throw new FormatException(
                    $"{source}: invalid context '{contextRaw}'. Use a comma-separated list of "
                    + "Transcript, Notes, Attachments, Summary, Minutes, Actions.");
        }

        return new BuiltInFormulaSpec(
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            body,
            context);
    }

    /// <summary>Read and parse every formulas/*.md in <paramref name="dir"/> (filename order). A missing
    /// directory yields an empty list; a malformed/unreadable file is skipped (logged) so one bad file can't
    /// crash boot.</summary>
    public static IReadOnlyList<BuiltInFormulaSpec> LoadFrom(string dir, ILogger? log = null)
    {
        if (!Directory.Exists(dir))
        {
            log?.LogWarning("Built-in formulas directory not found: {Dir}", dir);
            return [];
        }

        var specs = new List<BuiltInFormulaSpec>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md")
                     .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            // Skip a bad file (malformed, unreadable, or wrong-permissions on a mounted volume) rather than
            // crash boot; UnauthorizedAccessException is a SystemException, not an IOException, so name it.
            try { specs.Add(Parse(File.ReadAllText(path), Path.GetFileName(path))); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("Skipping built-in formula {File}: {Error}", Path.GetFileName(path), ex.Message);
            }
        }
        return specs;
    }
}
