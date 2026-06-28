using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of a transcript into downloadable formats. The text/markdown/RTF
/// documents mirror the emailed transcript: a Name heading, the Summary, then the Transcript — as
/// paragraphs for plain text, and as a Time/Speaker/Text table for Markdown and RTF.</summary>
public static class TranscriptFormatter
{
    private const string EmDash = "—";

    /// <summary>Plain text: headings, the summary, then one paragraph per segment.</summary>
    public static string ToText(string name, string? summary, IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        sb.Append("Transcript Name\n").Append(name).Append("\n\n");
        sb.Append("Summary\n").Append(string.IsNullOrWhiteSpace(summary) ? EmDash : summary!.Trim()).Append("\n\n");
        sb.Append("Transcript\n");
        foreach (var s in segments)
            sb.Append("\n[").Append(Clock(s.StartMs)).Append("] ").Append(s.SpeakerDisplay).Append('\n')
              .Append(s.Text.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>Markdown: headings, the summary, then a Time/Speaker/Text table.</summary>
    public static string ToMarkdown(string name, string? summary, IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append("\n\n");
        sb.Append("## Summary\n\n").Append(string.IsNullOrWhiteSpace(summary) ? "_(none)_" : summary!.Trim()).Append("\n\n");
        sb.Append("## Transcript\n\n");
        sb.Append("| Time | Speaker | Text |\n| --- | --- | --- |\n");
        foreach (var s in segments)
            sb.Append("| ").Append(Clock(s.StartMs)).Append(" | ").Append(Md(s.SpeakerDisplay))
              .Append(" | ").Append(Md(s.Text)).Append(" |\n");
        return sb.ToString();
    }

    /// <summary>Rich Text Format: bold headings, the summary, then a table of time, speaker, and text.</summary>
    public static string ToRtf(string name, string? summary, IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Arial;}}\f0\fs22").Append('\n');
        sb.Append(@"{\b Transcript Name}\line ").Append(Rtf(name)).Append(@"\par").Append('\n');
        sb.Append(@"{\b Summary}\line ")
          .Append(string.IsNullOrWhiteSpace(summary) ? Rtf(EmDash) : Rtf(summary!.Trim())).Append(@"\par").Append('\n');
        sb.Append(@"{\b Transcript}\par").Append('\n');
        sb.Append(Row(@"{\b Time}", @"{\b Speaker}", @"{\b Text}"));
        foreach (var s in segments)
            sb.Append(Row(Clock(s.StartMs), Rtf(s.SpeakerDisplay), Rtf(s.Text)));
        sb.Append('}');
        return sb.ToString();

        static string Row(string a, string b, string c) =>
            @"\trowd\trgaph100\cellx1500\cellx4800\cellx9600 " + a + @"\cell " + b + @"\cell " + c + @"\cell\row" + "\n";
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
