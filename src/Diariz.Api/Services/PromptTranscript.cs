using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Shared "[Speaker]: [Text]" transcript flattening used to feed segments to the LLM, bounded by
/// a character budget. Extracted so the summarise and action-extraction prompts build context identically.</summary>
public static class PromptTranscript
{
    /// <summary>Upper bound on transcript characters sent to the model (keeps requests bounded).</summary>
    public const int DefaultCharBudget = 24000;

    public static string Build(IReadOnlyList<SegmentDto> segments, int charBudget = DefaultCharBudget)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (sb.Length >= charBudget) break;
            sb.Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
        }
        if (sb.Length > charBudget) sb.Length = charBudget;
        return sb.ToString();
    }
}
