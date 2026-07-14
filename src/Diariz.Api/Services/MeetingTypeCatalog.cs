using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Loads the standard meeting types the app ships with from git-editable markdown files in
/// <c>meeting-types/</c> - the same format the built-in formulas use, because a minutes template <i>is</i> a
/// formula.
///
/// <para>They used to be hand-built in C# (<c>Sec(1, "Purpose", Prompt("..."))</c>), which buried the words the
/// model is given inside code. A file is reviewable in a PR diff, and a new standard is a new file.</para>
///
/// <code>
/// ---
/// key: general
/// group: Standard
/// title: General Meeting
/// icon: document
/// color: #5C6BC0
/// overview: A general-purpose meeting. Produce neutral, professional minutes...
/// context: Transcript, Notes, Actions
/// ---
/// # Meeting details
/// Date: {{date}}
///
/// # Purpose
/// [[WRITE: State the purpose of the meeting in 1-2 lines.]]
/// </code>
///
/// The body is parsed by <see cref="TemplateMarkdown"/>; <see cref="Parse"/> is pure (unit-tested) and
/// <see cref="LoadFrom"/> does the I/O, skipping a malformed file rather than crashing boot.</summary>
public static class MeetingTypeCatalog
{
    private const int ValidContextMask =
        (int)(FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Attachments
            | FormulaContext.Summary | FormulaContext.Minutes | FormulaContext.Actions);

    /// <summary>Parse one meeting-type file. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static StandardMeetingType Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n").TrimStart('﻿').TrimStart();
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new FormatException($"{source}: must start with a '---' frontmatter block.");

        var afterOpen = normalized[4..];
        var endIdx = afterOpen.IndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0)
            throw new FormatException($"{source}: frontmatter block is not closed with '---'.");

        var frontmatter = afterOpen[..endIdx];
        var body = afterOpen[(endIdx + 4)..].TrimStart('\n');

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in frontmatter.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"{source}: frontmatter line '{line}' is not 'key: value'.");
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string Required(string key) =>
            fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : throw new FormatException($"{source}: missing required '{key}'.");

        var content = TemplateMarkdown.Parse(body);
        if (content.Sections.Count == 0)
            throw new FormatException($"{source}: the template body (after the frontmatter) is empty.");
        if (content.Validate() is { Ok: false, Error: var error })
            throw new FormatException($"{source}: {error}");

        var icon = Required("icon");
        if (!MeetingTypeIcons.IsValid(icon))
            throw new FormatException($"{source}: unknown icon '{icon}'.");

        var context = FormulaContext.None;
        if (fields.TryGetValue("context", out var contextRaw) && !string.IsNullOrWhiteSpace(contextRaw))
        {
            if (!Enum.TryParse(contextRaw, ignoreCase: true, out context)
                || ((int)context & ~ValidContextMask) != 0)
                throw new FormatException(
                    $"{source}: invalid context '{contextRaw}'. Use a comma-separated list of "
                    + "Transcript, Notes, Attachments, Summary, Minutes, Actions.");
        }

        return new StandardMeetingType(
            Required("key"),
            Required("group"),
            Required("title"),
            icon,
            Required("color"),
            fields.GetValueOrDefault("overview", string.Empty),
            content.Serialize(),
            context);
    }

    /// <summary>Read and parse every <c>meeting-types/*.md</c> in <paramref name="dir"/> (filename order). A missing
    /// directory yields an empty list; a malformed file is skipped (logged) so one bad file can't crash boot.</summary>
    public static IReadOnlyList<StandardMeetingType> LoadFrom(string dir, ILogger? log = null)
    {
        if (!Directory.Exists(dir))
        {
            log?.LogWarning("Meeting-types directory not found: {Dir}", dir);
            return [];
        }

        var types = new List<StandardMeetingType>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md")
                     .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            try { types.Add(Parse(File.ReadAllText(path), Path.GetFileName(path))); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("Skipping meeting type {File}: {Error}", Path.GetFileName(path), ex.Message);
            }
        }
        return types;
    }
}
