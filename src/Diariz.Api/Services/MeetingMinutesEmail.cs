using System.Text;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of meeting minutes into an HTML email body. The minutes are already
/// GitHub-flavoured Markdown; the controller converts them to HTML (via <see cref="MarkdownRenderer"/>) and
/// passes that in, so this helper stays dependency-free and unit-testable.</summary>
public static class MeetingMinutesEmail
{
    public static string Subject(string name, ExportStrings? strings = null) =>
        (strings ?? ExportStrings.English).MinutesSubjectFor(name);

    public static string BuildHtml(string name, string minutesHtml, ExportStrings? strings = null)
    {
        var s10n = strings ?? ExportStrings.English;
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#111;\">");
        sb.Append(minutesHtml);
        sb.Append("<p style=\"color:#888;font-size:12px;margin-top:16px;\">").Append(Enc(s10n.SentFromDiariz)).Append("</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
