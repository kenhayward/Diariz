using System.Text;

namespace Diariz.Api.Services;

/// <summary>The backstop behind the prompt's "already recorded" list: an LLM told not to repeat an action
/// sometimes does anyway, word for word. Only catches the same words - paraphrases are the prompt's job.</summary>
public static class ActionMerge
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(c); space = false; }
            else space = true; // punctuation and whitespace both separate words
        }
        return sb.ToString();
    }

    public static IReadOnlyList<ExtractedAction> WithoutDuplicates(
        IReadOnlyList<ExtractedAction> extracted, IEnumerable<string> existingTexts)
    {
        var seen = new HashSet<string>(existingTexts.Select(Normalize));
        return extracted.Where(e => seen.Add(Normalize(e.Text))).ToList();
    }
}
