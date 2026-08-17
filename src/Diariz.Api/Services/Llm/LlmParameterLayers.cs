using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diariz.Api.Services.Llm;

/// <summary>Merges parameter layers, most specific first, into one decided set.
///
/// Three states per key, and the last two are genuinely different instructions:
/// <list type="bullet">
///   <item>absent - keep looking in the next layer down</item>
///   <item>present, null - omit this parameter entirely; stop looking</item>
///   <item>present with a value - use it; stop looking</item>
/// </list>
///
/// A sentinel could not express both of the last two: -1 is a legal, meaningful value for max_tokens
/// (unlimited) and top_k (disabled) on some OpenAI-compatible servers.</summary>
public static class LlmParameterLayers
{
    public const string Temperature = "temperature";
    public const string TopP = "top_p";
    public const string TopK = "top_k";
    public const string RepeatPenalty = "repeat_penalty";
    public const string FrequencyPenalty = "frequency_penalty";
    public const string PresencePenalty = "presence_penalty";
    public const string MaxTokens = "max_tokens";
    public const string MaxCompletionTokens = "max_completion_tokens";
    public const string ReasoningEffort = "reasoning_effort";
    public const string ReasoningEnabled = "reasoning_enabled";
    public const string TimeoutSeconds = "timeout_seconds";
    public const string ToolsSupported = "tools_supported";
    public const string ImagesSupported = "images_supported";

    /// <summary>Every key a layer may contain. The single source the API's validation and the admin UI's
    /// field list both read, so a parameter cannot exist in one and not the other.</summary>
    public static readonly IReadOnlyList<string> ParameterNames =
    [
        Temperature, TopP, TopK, RepeatPenalty, FrequencyPenalty, PresencePenalty,
        MaxTokens, MaxCompletionTokens, ReasoningEffort,
        ReasoningEnabled, TimeoutSeconds, ToolsSupported, ImagesSupported,
    ];

    public static LlmParameters Resolve(IReadOnlyList<string?> layersMostSpecificFirst)
    {
        var layers = new List<JsonObject>();
        foreach (var layer in layersMostSpecificFirst)
        {
            if (string.IsNullOrWhiteSpace(layer)) continue;
            try
            {
                // A malformed row must not take the platform down; treat it as "sets nothing".
                if (JsonNode.Parse(layer) is JsonObject o) layers.Add(o);
            }
            catch (JsonException)
            {
                // Ignored on purpose - see above.
            }
        }

        return new LlmParameters
        {
            Temperature = Number(layers, Temperature),
            TopP = Number(layers, TopP),
            TopK = Integer(layers, TopK),
            RepeatPenalty = Number(layers, RepeatPenalty),
            FrequencyPenalty = Number(layers, FrequencyPenalty),
            PresencePenalty = Number(layers, PresencePenalty),
            MaxTokens = Integer(layers, MaxTokens),
            MaxCompletionTokens = Integer(layers, MaxCompletionTokens),
            ReasoningEffort = Text(layers, ReasoningEffort),
            ReasoningEnabled = Flag(layers, ReasoningEnabled) ?? false,
            TimeoutSeconds = Integer(layers, TimeoutSeconds) ?? LlmParameters.DefaultTimeoutSeconds,
            ToolsSupported = Flag(layers, ToolsSupported) ?? true,
            ImagesSupported = Flag(layers, ImagesSupported) ?? false,
        };
    }

    /// <summary>The first layer that MENTIONS the key decides it, whether it names a value or null. Returns
    /// null both when no layer mentions the key and when the deciding layer says null - the callers below
    /// cannot distinguish those, and do not need to: both mean "no value".</summary>
    private static JsonNode? Decide(List<JsonObject> layers, string key)
    {
        foreach (var layer in layers)
            if (layer.TryGetPropertyValue(key, out var node))
                return node;

        return null;
    }

    private static double? Number(List<JsonObject> layers, string key) =>
        Decide(layers, key) is { } n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<double>() : null;

    private static int? Integer(List<JsonObject> layers, string key) =>
        Decide(layers, key) is { } n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : null;

    private static string? Text(List<JsonObject> layers, string key)
    {
        if (Decide(layers, key) is not { } node || node.GetValueKind() != JsonValueKind.String) return null;
        var s = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool? Flag(List<JsonObject> layers, string key) =>
        Decide(layers, key) is { } n && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? n.GetValue<bool>()
            : null;
}
