using System.Text;
using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>One OpenAI chat message.</summary>
public record ChatMessage(string Role, string Content);

/// <summary>What the LLM produced: a summary and (optionally) a short recording name.</summary>
public record SummaryResult(string Summary, string? Name);

/// <summary>
/// Pure (no IO) construction of the chat request and parsing of the response, so the prompt
/// shape and the brittle JSON-extraction can be unit-tested without a live endpoint.
/// </summary>
public static class SummarizationPrompt
{
    /// <summary>Upper bound on transcript characters sent to the model (keeps requests bounded).</summary>
    public const int DefaultTranscriptCharBudget = 24000;

    public static IReadOnlyList<ChatMessage> BuildMessages(
        IReadOnlyList<SegmentDto> segments, bool needName, int charBudget = DefaultTranscriptCharBudget)
    {
        var shape = needName
            ? "{\"summary\": string, \"name\": string}, where \"name\" is a concise title of at most 6 words"
            : "{\"summary\": string}";
        var system =
            "You are a meeting-notes assistant. Summarise the transcript into a few clear sentences " +
            "covering the key points and any decisions or action items. " +
            $"Respond with ONLY strict minified JSON of the form {shape}. Do not wrap it in code fences.";

        var user = "Transcript:\n" + BuildTranscript(segments, charBudget);
        return [new ChatMessage("system", system), new ChatMessage("user", user)];
    }

    public static SummaryResult ParseResponse(string responseJson, bool needName)
    {
        string content;
        using (var doc = JsonDocument.Parse(responseJson))
        {
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";
        }

        content = StripCodeFence(content);

        try
        {
            using var inner = JsonDocument.Parse(content);
            var summary = inner.RootElement.TryGetProperty("summary", out var s)
                ? s.GetString()?.Trim() ?? ""
                : content;
            string? name = null;
            if (needName && inner.RootElement.TryGetProperty("name", out var n))
            {
                var raw = n.GetString()?.Trim();
                name = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            return new SummaryResult(summary, name);
        }
        catch (JsonException)
        {
            // Model didn't return JSON — use the raw text as the summary.
            return new SummaryResult(content, null);
        }
    }

    private static string BuildTranscript(IReadOnlyList<SegmentDto> segments, int charBudget)
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

    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var body = s[(firstNl + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? body[..fence] : body).Trim();
    }
}
