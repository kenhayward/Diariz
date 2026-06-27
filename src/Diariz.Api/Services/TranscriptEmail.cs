using System.Net;
using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of a transcript into an HTML email body — bold headings for the
/// name/summary/transcript and a table of time, speaker, and text.</summary>
public static class TranscriptEmail
{
    public static string Subject(string name) => $"Transcript for {name}";

    public static string BuildHtml(string name, string? summary, IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#111;\">");

        sb.Append("<p><strong>Transcript Name</strong><br>").Append(Enc(name)).Append("</p>");

        sb.Append("<p><strong>Summary</strong><br>")
          .Append(string.IsNullOrWhiteSpace(summary) ? "&mdash;" : Enc(summary!).Replace("\n", "<br>"))
          .Append("</p>");

        sb.Append("<p><strong>Transcript</strong></p>");
        sb.Append("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
        sb.Append("<tr><th align=\"left\">Time</th><th align=\"left\">Speaker</th><th align=\"left\">Text</th></tr>");
        foreach (var s in segments)
            sb.Append("<tr><td>").Append(Clock(s.StartMs)).Append("</td><td>")
              .Append(Enc(s.SpeakerDisplay)).Append("</td><td>").Append(Enc(s.Text)).Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("<p style=\"color:#888;font-size:12px;margin-top:16px;\">Sent from Diariz</p>");
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
