using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>One extracted action item (all fields free text; actor/deadline may be empty).</summary>
public record ExtractedAction(string Text, string Actor, string Deadline);

/// <summary>
/// Pure (no IO) construction of the action-extraction chat request and parsing of the response, so the
/// prompt shape and the brittle JSON-array extraction can be unit-tested without a live endpoint. Mirrors
/// <see cref="SummarizationPrompt"/>.
/// </summary>
public static class ActionsPrompt
{
    public static IReadOnlyList<ChatMessage> BuildMessages(
        IReadOnlyList<SegmentDto> segments, int charBudget = PromptTranscript.DefaultCharBudget)
    {
        var system =
            "You extract action items from a meeting transcript. An action item is a concrete task someone " +
            "agreed or was asked to do. For each one capture the task, who is responsible (the actor), and any " +
            "deadline. The actor and deadline may be unknown — use an empty string when so. " +
            "There may be NO action items; in that case return an empty array. " +
            "Respond with ONLY a strict minified JSON array of the form " +
            "[{\"action\": string, \"actor\": string, \"deadline\": string}]. Do not wrap it in code fences.";

        var user = "Transcript:\n" + PromptTranscript.Build(segments, charBudget);
        return [new ChatMessage("system", system), new ChatMessage("user", user)];
    }

    public static IReadOnlyList<ExtractedAction> ParseResponse(string responseJson)
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

        var json = ExtractJsonArray(content);
        if (json is null) return [];

        try
        {
            using var inner = JsonDocument.Parse(json);
            if (inner.RootElement.ValueKind != JsonValueKind.Array) return [];

            var actions = new List<ExtractedAction>();
            foreach (var el in inner.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var text = Str(el, "action");
                if (string.IsNullOrWhiteSpace(text)) continue; // an action with no task is useless
                actions.Add(new ExtractedAction(text.Trim(), Str(el, "actor").Trim(), Str(el, "deadline").Trim()));
            }
            return actions;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The substring from the first "[" to the last "]", or null if there's no array.</summary>
    private static string? ExtractJsonArray(string s)
    {
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
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
