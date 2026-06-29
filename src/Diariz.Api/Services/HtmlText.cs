using System.Net;
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Best-effort HTML → plain text: drops script/style, turns block tags into line breaks, strips
/// the remaining tags, decodes entities, and collapses whitespace. Not a sanitiser for re-rendering — it
/// produces plain text for use as LLM context, so no markup survives.</summary>
public static partial class HtmlText
{
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = ScriptStyle().Replace(html, " ");
        s = LineBreaks().Replace(s, "\n");          // <br>, </p>, </div>, </li>, </tr>, headings → newline
        s = Tags().Replace(s, "");                  // strip remaining tags
        s = WebUtility.HtmlDecode(s).Replace(" ", " "); // decode entities; non-breaking spaces → normal
        s = BlankRuns().Replace(s, " ");            // collapse runs of spaces/tabs
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
