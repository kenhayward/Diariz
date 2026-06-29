using System.Text;
using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>
/// Pure (no IO) construction of the translation chat request and parsing of the response, so the prompt
/// shape and the brittle JSON parsing can be unit-tested without a live endpoint. Mirrors
/// <see cref="ActionsPrompt"/>. Items are translated as an indexed batch so the caller can map each
/// translation back to its source by index.
/// </summary>
public static class TranslationPrompt
{
    /// <summary>Build the chat messages to translate a batch of indexed strings into
    /// <paramref name="targetLanguage"/> (an English language name, e.g. "Spanish").</summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(
        string targetLanguage, IReadOnlyList<(int Index, string Text)> items)
    {
        var system =
            $"You are a professional translator. Translate the text of each item into {targetLanguage}. " +
            "Preserve meaning, tone, and any formatting. Keep proper nouns, personal names, brand names, and " +
            "code/technical identifiers natural — do not invent or transliterate names. Translate ONLY the " +
            "text; do not add commentary. You receive a JSON array of objects {\"i\": number, \"t\": string}. " +
            "Respond with ONLY a strict minified JSON array of {\"i\": number, \"t\": string} using the SAME " +
            "i values, where t is the translation. Do not wrap it in code fences.";

        var arr = new StringBuilder("[");
        for (var k = 0; k < items.Count; k++)
        {
            if (k > 0) arr.Append(',');
            arr.Append("{\"i\":").Append(items[k].Index).Append(",\"t\":")
               .Append(JsonSerializer.Serialize(items[k].Text)).Append('}');
        }
        arr.Append(']');

        return [new ChatMessage("system", system), new ChatMessage("user", arr.ToString())];
    }

    /// <summary>Parse the model's response into a map of item index → translated text. Tolerates code
    /// fences and surrounding prose; ignores malformed entries.</summary>
    public static IReadOnlyDictionary<int, string> ParseTranslations(string responseJson)
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
        var map = new Dictionary<int, string>();
        if (json is null) return map;

        try
        {
            using var inner = JsonDocument.Parse(json);
            if (inner.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var el in inner.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("i", out var iEl) || iEl.ValueKind != JsonValueKind.Number) continue;
                if (!el.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String) continue;
                map[iEl.GetInt32()] = tEl.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Leave whatever parsed; the caller falls back to the source text for missing indices.
        }
        return map;
    }

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
