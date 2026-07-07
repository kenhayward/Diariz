using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Deterministically renders the "Enhanced notes" minutes section with full provenance: the user's
/// literal words in bold (never paraphrased), the AI expansion in plain text, capture stamps in italics, and
/// [mm:ss] transcript deep-links for every supporting moment. Unsupported lines are kept and marked - the
/// forensic guarantee that nothing the user wrote is silently dropped.</summary>
public static class NotesComposer
{
    public const string NoNotes = "No notes were taken for this meeting.";
    private const string NotDiscussed = "*not discussed in the recording*";

    public static string Render(
        IReadOnlyList<MeetingNoteDto> notes, IReadOnlyList<EnhancedNote> enhanced, Guid recordingId)
    {
        if (notes.Count == 0) return NoNotes;
        var sb = new StringBuilder();
        for (var i = 0; i < notes.Count; i++)
        {
            var e = i < enhanced.Count ? enhanced[i] : new EnhancedNote(i, null, [], true);
            sb.Append("- ").Append(Lead(notes[i]));
            if (e.NotDiscussed || string.IsNullOrWhiteSpace(e.Expansion))
            {
                sb.Append(" - ").Append(NotDiscussed);
            }
            else
            {
                sb.Append(" - ").Append(e.Expansion!.Trim());
                foreach (var ms in e.TimesMs)
                    sb.Append($" [{NotesEnhancer.Mmss(ms)}](/recordings/{recordingId}?t={ms})");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd().ReplaceLineEndings("\n");
    }

    /// <summary>Fallback when the enhancer call fails: the raw lines with stamps - a notes failure must
    /// never fail the minutes.</summary>
    public static string RenderRaw(IReadOnlyList<MeetingNoteDto> notes)
    {
        if (notes.Count == 0) return NoNotes;
        return string.Join("\n", notes.Select(n => $"- {Lead(n)}"));
    }

    private static string Lead(MeetingNoteDto n)
    {
        var stamp = n.CapturedAtMs is { } ms ? $" *({NotesEnhancer.Mmss(ms)})*" : "";
        return $"**{Escape(n.Text)}**{stamp}";
    }

    private static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
}
