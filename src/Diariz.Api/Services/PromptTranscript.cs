using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Shared "[Speaker]: [Text]" transcript flattening used to feed segments to the LLM, bounded by
/// a character budget. Extracted so the summarise and action-extraction prompts build context identically.
///
/// <para>Consecutive rows under one name become one line. The worker splits a segment wherever the
/// word-level speaker changes (#803), so one turn can arrive as several rows; repeating "Name: " on each
/// spent the fixed budget on prefixes and cut long meetings off sooner. Rows are joined by the
/// <i>displayed</i> name, because that is the only speaker identity the model sees.</para></summary>
public static class PromptTranscript
{
    /// <summary>Upper bound on transcript characters sent to the model (keeps requests bounded).</summary>
    public const int DefaultCharBudget = 24000;

    public static string Build(IReadOnlyList<SegmentDto> segments, int charBudget = DefaultCharBudget)
    {
        var sb = new StringBuilder();
        string? current = null;
        foreach (var s in segments)
        {
            if (sb.Length >= charBudget) break;
            if (s.SpeakerDisplay == current)
            {
                sb.Length--; // reopen the speaker's line: drop its newline
                sb.Append(' ').Append(s.Text).Append('\n');
                continue;
            }
            sb.Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
            current = s.SpeakerDisplay;
        }
        if (sb.Length > charBudget) sb.Length = charBudget;
        return sb.ToString();
    }
}
