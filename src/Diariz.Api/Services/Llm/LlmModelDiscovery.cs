using System.Text.Json;

namespace Diariz.Api.Services.Llm;

/// <summary>One model an endpoint reported. <see cref="ContextLength"/> and <see cref="Kind"/> are null when
/// the endpoint does not say - the OpenAI-compatible listing reports neither.</summary>
public sealed record DiscoveredModel(string Id, int? ContextLength, string? Kind);

/// <summary>Parsing and filtering for model discovery, with no HTTP in sight.
///
/// Pure so that the interesting part - deciding what is a chat model - is testable without a server to talk
/// to, the same separation the Python worker uses for its segment shaping.</summary>
public static class LlmModelDiscovery
{
    /// <summary>What an imported model's context window is set to when the endpoint does not report one.
    ///
    /// 16k rather than the editor's 8k default: this number drives both the chat context dial and the real
    /// context budget, so an import that silently under-sizes a model truncates transcript text the user
    /// believed was in scope. An administrator can correct it per model, and the import summary says which
    /// models were guessed at.</summary>
    public const int DefaultContextLength = 16384;

    /// <summary>Substrings that identify a non-chat model when the endpoint reports no type.
    ///
    /// A heuristic, and only ever consulted when there is nothing better - a declared type always wins. It
    /// is deliberately short: a wrongly-kept model is one row an administrator deletes, while a
    /// wrongly-dropped one never appears at all and so is invisible.</summary>
    private static readonly string[] NonChatMarkers =
        ["embed", "rerank", "whisper", "tts", "clip", "bge-"];

    /// <summary>The types LM Studio reports for a model that can hold a conversation. A vision-language
    /// model counts: it is a chat model that also accepts images.</summary>
    private static readonly string[] ChatKinds = ["llm", "vlm"];

    /// <summary>LM Studio's own listing (<c>/api/v0/models</c>), which reports a type and a real context
    /// length.</summary>
    public static IReadOnlyList<DiscoveredModel> ParseLmStudio(string json) =>
        Parse(json, e => new DiscoveredModel(
            e.GetProperty("id").GetString() ?? "",
            e.TryGetProperty("max_context_length", out var c) && c.TryGetInt32(out var n) ? n : null,
            e.TryGetProperty("type", out var t) ? t.GetString() : null));

    /// <summary>The OpenAI-compatible listing (<c>/models</c>), which reports neither a type nor a context
    /// length - only ids.</summary>
    public static IReadOnlyList<DiscoveredModel> ParseOpenAi(string json) =>
        Parse(json, e => new DiscoveredModel(e.GetProperty("id").GetString() ?? "", null, null));

    private static IReadOnlyList<DiscoveredModel> Parse(string json, Func<JsonElement, DiscoveredModel> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            return data.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out _))
                .Select(read)
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .ToList();
        }
        catch (JsonException)
        {
            // A malformed body is a fact about the endpoint, not an error in us. Returning nothing lets the
            // caller report "no models found" rather than a 500 from our API that says nothing about theirs.
            return [];
        }
    }

    /// <summary>Whether this is a model chat could actually use.
    ///
    /// A DECLARED type wins outright, in both directions: when the server says "llm" it knows better than a
    /// substring match, so a chat model whose name happens to contain "embed" survives; and when it says
    /// something we do not recognise, that is still a statement of fact, so falling back to the name
    /// heuristic would be overriding an answer with a guess. Only when no type is reported at all does the
    /// name decide.</summary>
    public static bool IsChatModel(DiscoveredModel model)
    {
        if (model.Kind is { } kind)
            return ChatKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

        var id = model.Id.ToLowerInvariant();
        return !NonChatMarkers.Any(id.Contains);
    }
}
