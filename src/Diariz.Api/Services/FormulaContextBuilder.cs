using System.Text;
using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>The pieces of a recording a <see cref="FormulaContextBuilder"/> can draw from, already loaded
/// by the caller (<see cref="FormulaRunner"/>). Pure data - no EF, no I/O.</summary>
public sealed record FormulaContextData(
    IReadOnlyList<SegmentDto> Segments,
    string? Summary,
    string? Minutes,
    IReadOnlyList<string> NoteLines,
    IReadOnlyList<RecordingActionDto> Actions);

/// <summary>Pure (EF-free) assembly of a Formula's run context: picks the sections named by the formula's
/// <see cref="FormulaContext"/> flags out of <see cref="FormulaContextData"/>, each under a short Markdown
/// header, and caps the whole thing to a char budget so requests stay bounded.</summary>
public static class FormulaContextBuilder
{
    /// <summary>Upper bound on context characters embedded in the user message (mirrors
    /// <see cref="ChatContextBuilder.DefaultCharBudget"/>).</summary>
    public const int DefaultCharBudget = 48_000;

    /// <summary>Returned when the selected sections yield no content (nothing flagged, or every flagged
    /// section is empty). Mirrors <see cref="ChatContextBuilder"/>'s "No transcript context was provided..."
    /// guard so the model never receives an empty user message.</summary>
    public const string EmptyContextFallback = "No context was available for this recording.";

    public static string Build(FormulaContext flags, FormulaContextData data, int charBudget = DefaultCharBudget)
    {
        var sb = new StringBuilder();

        if (flags.HasFlag(FormulaContext.Transcript))
        {
            var text = TranscriptFormatter.ToPlainText(data.Segments);
            if (!string.IsNullOrWhiteSpace(text)) AppendSection(sb, "Transcript", text);
        }

        if (flags.HasFlag(FormulaContext.Summary) && !string.IsNullOrWhiteSpace(data.Summary))
            AppendSection(sb, "Summary", data.Summary!);

        if (flags.HasFlag(FormulaContext.Minutes) && !string.IsNullOrWhiteSpace(data.Minutes))
            AppendSection(sb, "Minutes", data.Minutes!);

        if (flags.HasFlag(FormulaContext.Notes) && data.NoteLines.Count > 0)
            AppendSection(sb, "Notes", string.Join('\n', data.NoteLines.Select(l => "- " + l.Trim())));

        if (flags.HasFlag(FormulaContext.Actions) && data.Actions.Count > 0)
        {
            // ActionsForChat leads with its own "Actions:\n" line; our section header already says that.
            var text = TranscriptFormatter.ActionsForChat(data.Actions);
            const string prefix = "Actions:\n";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
            if (text.Length > 0) AppendSection(sb, "Actions", text);
        }

        // Attachments: Phase 1 - not implemented. The flag is intentionally a no-op (never fails the run).
        // TODO Phase: attachments extraction

        var body = sb.ToString().Trim();
        if (body.Length == 0) return EmptyContextFallback;
        if (body.Length > charBudget) body = body[..charBudget] + "\n[context truncated]";
        return body;
    }

    private static void AppendSection(StringBuilder sb, string title, string text) =>
        sb.Append("## ").Append(title).Append('\n').Append(text.Trim()).Append("\n\n");
}
