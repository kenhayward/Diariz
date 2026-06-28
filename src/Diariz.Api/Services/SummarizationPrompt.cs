using System.Text.Json;
using System.Text.RegularExpressions;
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
    public const int DefaultTranscriptCharBudget = PromptTranscript.DefaultCharBudget;

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

        var user = "Transcript:\n" + PromptTranscript.Build(segments, charBudget);
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

        // Content may be wrapped in prose or model-specific tokens (e.g. gpt-oss "harmony"
        // markers like <|channel|>final<|constrain|>{...}); pull out the embedded JSON object.
        var json = ExtractJsonObject(content);
        if (json is not null)
        {
            try
            {
                using var inner = JsonDocument.Parse(json);
                if (inner.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var summary = inner.RootElement.TryGetProperty("summary", out var s)
                        && s.ValueKind == JsonValueKind.String
                            ? s.GetString()!.Trim()
                            : StripModelTokens(content);
                    string? name = null;
                    if (needName && inner.RootElement.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String)
                    {
                        var raw = n.GetString()?.Trim();
                        name = string.IsNullOrWhiteSpace(raw) ? null : raw;
                    }
                    return new SummaryResult(summary, name);
                }
            }
            catch (JsonException) { /* fall through to the plain-text fallback */ }
        }

        // No usable JSON — return the text with any model tokens stripped.
        return new SummaryResult(StripModelTokens(content), null);
    }

    /// <summary>The substring from the first "{" to the last "}", or null if there's no object.</summary>
    private static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }

    /// <summary>Strips model control tokens like &lt;|channel|&gt; so a non-JSON fallback reads cleanly.</summary>
    private static string StripModelTokens(string s) =>
        Regex.Replace(s, @"<\|[^|]*\|>", " ").Trim();

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
