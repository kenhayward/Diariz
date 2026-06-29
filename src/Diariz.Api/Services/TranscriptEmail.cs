using System.Net;
using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of a transcript into an HTML email body — bold headings for the
/// name/summary/transcript and a table of time, speaker, and text.</summary>
public static class TranscriptEmail
{
    public static string Subject(string name, ExportStrings? strings = null) =>
        (strings ?? ExportStrings.English).SubjectFor(name);

    public static string BuildHtml(string name, string? summary, IReadOnlyList<SegmentDto> segments,
        IReadOnlyList<RecordingActionDto>? actions = null, ExportStrings? strings = null)
    {
        var s10n = strings ?? ExportStrings.English;
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#111;\">");

        sb.Append("<p><strong>").Append(Enc(s10n.TranscriptName)).Append("</strong><br>").Append(Enc(name)).Append("</p>");

        sb.Append("<p><strong>").Append(Enc(s10n.Summary)).Append("</strong><br>")
          .Append(string.IsNullOrWhiteSpace(summary) ? "&mdash;" : Enc(summary!).Replace("\n", "<br>"))
          .Append("</p>");

        if (actions is { Count: > 0 })
        {
            sb.Append("<p><strong>").Append(Enc(s10n.Actions)).Append("</strong></p>");
            sb.Append("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
            sb.Append("<tr><th align=\"left\">").Append(Enc(s10n.Action)).Append("</th><th align=\"left\">")
              .Append(Enc(s10n.Actor)).Append("</th><th align=\"left\">").Append(Enc(s10n.Deadline)).Append("</th></tr>");
            foreach (var a in actions)
                sb.Append("<tr><td>").Append(Enc(a.Text).Replace("\n", "<br>")).Append("</td><td>")
                  .Append(Enc(a.Actor)).Append("</td><td>")
                  .Append(Enc(a.Deadline)).Append("</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<p><strong>").Append(Enc(s10n.Transcript)).Append("</strong></p>");
        sb.Append("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
        sb.Append("<tr><th align=\"left\">").Append(Enc(s10n.Time)).Append("</th><th align=\"left\">")
          .Append(Enc(s10n.Speaker)).Append("</th><th align=\"left\">").Append(Enc(s10n.Text)).Append("</th></tr>");
        foreach (var s in segments)
            sb.Append("<tr><td>").Append(Clock(s.StartMs)).Append("</td><td>")
              .Append(Enc(s.SpeakerDisplay)).Append("</td><td>")
              .Append(Enc(s.Text).Replace("\n", "<br>")) // preserve paragraph breaks from merged segments
              .Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("<p style=\"color:#888;font-size:12px;margin-top:16px;\">").Append(Enc(s10n.SentFromDiariz)).Append("</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }
}
