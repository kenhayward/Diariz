using System.Text;
using System.Text.RegularExpressions;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of a transcript into downloadable formats. The text/markdown/RTF
/// documents mirror the emailed transcript: a Name heading, the Summary, then the Transcript — as
/// paragraphs for plain text, and as a Time/Speaker/Text table for Markdown and RTF.</summary>
public static class TranscriptFormatter
{
    private const string EmDash = "—";

    /// <summary>Plain text: headings, the summary, the actions (if any), then one paragraph per segment.</summary>
    public static string ToText(string name, string? summary, IReadOnlyList<SegmentDto> segments,
        IReadOnlyList<RecordingActionDto>? actions = null, ExportStrings? strings = null)
    {
        var s10n = strings ?? ExportStrings.English;
        var sb = new StringBuilder();
        sb.Append(s10n.TranscriptName).Append('\n').Append(name).Append("\n\n");
        sb.Append(s10n.Summary).Append('\n').Append(string.IsNullOrWhiteSpace(summary) ? EmDash : summary!.Trim()).Append("\n\n");
        if (actions is { Count: > 0 })
        {
            sb.Append(s10n.Actions).Append('\n');
            foreach (var a in actions)
                sb.Append('\n').Append(a.Text.Trim()).Append('\n')
                  .Append(s10n.Actor).Append(": ").Append(Dash(a.Actor))
                  .Append("   ").Append(s10n.Deadline).Append(": ").Append(Dash(a.Deadline)).Append('\n');
            sb.Append('\n');
        }
        sb.Append(s10n.Transcript).Append('\n');
        foreach (var s in segments)
            sb.Append("\n[").Append(Clock(s.StartMs)).Append("] ").Append(s.SpeakerDisplay).Append('\n')
              .Append(s.Text.Trim()).Append('\n');
        // Collapse any run of blank lines (CRLF/LF/CR) into a single line break — text only.
        return Regex.Replace(sb.ToString(), @"(\r\n|\r|\n){2,}", "\n");
    }

    /// <summary>Markdown: headings, the summary, an actions table (if any), then a Time/Speaker/Text table.</summary>
    public static string ToMarkdown(string name, string? summary, IReadOnlyList<SegmentDto> segments,
        IReadOnlyList<RecordingActionDto>? actions = null, ExportStrings? strings = null)
    {
        var s10n = strings ?? ExportStrings.English;
        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append("\n\n");
        sb.Append("## ").Append(s10n.Summary).Append("\n\n")
          .Append(string.IsNullOrWhiteSpace(summary) ? $"_{s10n.None}_" : summary!.Trim()).Append("\n\n");
        if (actions is { Count: > 0 })
        {
            sb.Append("## ").Append(s10n.Actions).Append("\n\n");
            // 60/18/22% column widths via the separator-row dash counts (Action is the widest column).
            sb.Append("| ").Append(s10n.Action).Append(" | ").Append(s10n.Actor).Append(" | ").Append(s10n.Deadline).Append(" |\n")
              .Append("| ").Append(new string('-', 60)).Append(" | ").Append(new string('-', 18))
              .Append(" | ").Append(new string('-', 22)).Append(" |\n");
            foreach (var a in actions)
                sb.Append("| ").Append(Md(a.Text)).Append(" | ").Append(Md(a.Actor))
                  .Append(" | ").Append(Md(a.Deadline)).Append(" |\n");
            sb.Append('\n');
        }
        sb.Append("## ").Append(s10n.Transcript).Append("\n\n");
        // 13/16/71% column widths are carried by the separator-row dash counts (how pandoc/MultiMarkdown
        // size columns); GFM/kramdown ignore the extra dashes, so no stray attribute line is needed.
        sb.Append("| ").Append(s10n.Time).Append(" | ").Append(s10n.Speaker).Append(" | ").Append(s10n.Text).Append(" |\n")
          .Append("| ").Append(new string('-', 13)).Append(" | ").Append(new string('-', 16))
          .Append(" | ").Append(new string('-', 71)).Append(" |\n");
        foreach (var s in segments)
            sb.Append("| ").Append(Clock(s.StartMs)).Append(" | ").Append(Md(s.SpeakerDisplay))
              .Append(" | ").Append(Md(s.Text)).Append(" |\n");
        return sb.ToString();
    }

    /// <summary>Rich Text Format: bold headings, the summary, an actions table (if any), then a table of
    /// time, speaker, and text.</summary>
    public static string ToRtf(string name, string? summary, IReadOnlyList<SegmentDto> segments,
        IReadOnlyList<RecordingActionDto>? actions = null, ExportStrings? strings = null)
    {
        var s10n = strings ?? ExportStrings.English;
        string Bold(string label) => @"{\b " + Rtf(label) + "}"; // bold, RTF-escaped (labels may have accents)

        // Column boundaries (twips, of a ~9600-twip table): transcript 13/16/71%, actions 60/18/22%.
        const string transcriptCols = @"\cellx1248\cellx2784\cellx9600";
        const string actionCols = @"\cellx5760\cellx7488\cellx9600";

        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Arial;}}\f0\fs22").Append('\n');
        sb.Append(Bold(s10n.TranscriptName)).Append(@"\line ").Append(Rtf(name)).Append(@"\par").Append('\n');
        // Extra paragraph mark after the summary for breathing room before the table.
        sb.Append(Bold(s10n.Summary)).Append(@"\line ")
          .Append(string.IsNullOrWhiteSpace(summary) ? Rtf(EmDash) : Rtf(summary!.Trim())).Append(@"\par\par").Append('\n');
        if (actions is { Count: > 0 })
        {
            sb.Append(Bold(s10n.Actions)).Append(@"\par").Append('\n');
            sb.Append(Row(true, actionCols, Bold(s10n.Action), Bold(s10n.Actor), Bold(s10n.Deadline)));
            foreach (var a in actions)
                sb.Append(Row(false, actionCols, Rtf(a.Text), Rtf(a.Actor), Rtf(a.Deadline)));
            sb.Append(@"\par").Append('\n');
        }
        sb.Append(Bold(s10n.Transcript)).Append(@"\par").Append('\n');
        sb.Append(Row(true, transcriptCols, Bold(s10n.Time), Bold(s10n.Speaker), Bold(s10n.Text)));
        foreach (var s in segments)
            sb.Append(Row(false, transcriptCols, Clock(s.StartMs), Rtf(s.SpeakerDisplay), Rtf(s.Text)));
        sb.Append('}');
        return sb.ToString();

        // The header row uses \trhdr so it repeats on every page (\trkeep keeps each row off a page break).
        static string Row(bool header, string cols, string a, string b, string c) =>
            @"\trowd\trgaph100\trkeep" + (header ? @"\trhdr" : "") +
            cols + " " + a + @"\cell " + b + @"\cell " + c + @"\cell\row" + "\n";
    }

    /// <summary>Compact "[mm:ss] Speaker: text" lines — used to feed transcripts to the chat LLM.</summary>
    public static string ToPlainText(IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
            sb.Append('[').Append(Clock(s.StartMs)).Append("] ")
              .Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
        return sb.ToString();
    }

    /// <summary>Compact "- action (Actor: x; Deadline: y)" lines under an "Actions:" heading — used to feed
    /// extracted actions to the chat LLM alongside the transcript. Empty when there are no actions.</summary>
    public static string ActionsForChat(IReadOnlyList<RecordingActionDto> actions)
    {
        if (actions.Count == 0) return "";
        var sb = new StringBuilder("Actions:\n");
        foreach (var a in actions)
        {
            sb.Append("- ").Append(a.Text.Trim());
            var actor = a.Actor.Trim();
            var deadline = a.Deadline.Trim();
            if (actor.Length > 0 || deadline.Length > 0)
            {
                sb.Append(" (");
                if (actor.Length > 0) sb.Append("Actor: ").Append(actor);
                if (actor.Length > 0 && deadline.Length > 0) sb.Append("; ");
                if (deadline.Length > 0) sb.Append("Deadline: ").Append(deadline);
                sb.Append(')');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>SubRip (.srt) subtitle cues, numbered from 1, blank-line separated.</summary>
    public static string ToSrt(IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append(i + 1).Append('\n')
              .Append(SrtTime(s.StartMs)).Append(" --> ").Append(SrtTime(s.EndMs)).Append('\n')
              .Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
            if (i < segments.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    // Em-dash placeholder for an empty free-text field (actor/deadline).
    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? EmDash : s.Trim();

    // Markdown table cells can't contain a raw pipe or newline.
    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

    // Escape RTF control characters; non-ASCII becomes a \uN unicode escape with a '?' fallback.
    private static string Rtf(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                default:
                    if (ch > 127) sb.Append(@"\u").Append((int)ch).Append('?');
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    private static string SrtTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
    }
}
