using System.Text.Json.Nodes;
using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;

namespace Diariz.Api.Configuration;

/// <summary>One scope's worth of default parameters. Every property is nullable so that "not configured"
/// stays distinguishable from "configured to a value" - only what is actually set becomes a layer key, and
/// an unset property therefore inherits rather than omitting.</summary>
public class LlmParameterDefaults
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public string? ReasoningEffort { get; set; }
    public bool? ReasoningEnabled { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool? ToolsSupported { get; set; }
    public bool? ImagesSupported { get; set; }

    /// <summary>This set as a parameter-layer JSON object, or null when nothing is configured.
    ///
    /// Note that configuration can only ever produce "absent" or "a value" - never the explicit null that
    /// means "omit". That is deliberate: an operator who wants a parameter never sent simply leaves it
    /// unset, because the app defaults are the bottom of the layer stack and nothing sits below them.</summary>
    public string? ToLayer()
    {
        var o = new JsonObject();
        Add(o, LlmParameterLayers.Temperature, Temperature);
        Add(o, LlmParameterLayers.TopP, TopP);
        Add(o, LlmParameterLayers.TopK, TopK);
        Add(o, LlmParameterLayers.RepeatPenalty, RepeatPenalty);
        Add(o, LlmParameterLayers.FrequencyPenalty, FrequencyPenalty);
        Add(o, LlmParameterLayers.PresencePenalty, PresencePenalty);
        Add(o, LlmParameterLayers.MaxTokens, MaxTokens);
        Add(o, LlmParameterLayers.MaxCompletionTokens, MaxCompletionTokens);
        Add(o, LlmParameterLayers.ReasoningEnabled, ReasoningEnabled);
        Add(o, LlmParameterLayers.TimeoutSeconds, TimeoutSeconds);
        Add(o, LlmParameterLayers.ToolsSupported, ToolsSupported);
        Add(o, LlmParameterLayers.ImagesSupported, ImagesSupported);

        if (!string.IsNullOrWhiteSpace(ReasoningEffort))
            o[LlmParameterLayers.ReasoningEffort] = JsonValue.Create(ReasoningEffort);

        return o.Count == 0 ? null : o.ToJsonString();
    }

    private static void Add<T>(JsonObject o, string key, T? value) where T : struct
    {
        if (value.HasValue) o[key] = JsonValue.Create(value.Value);
    }
}

/// <summary>Application defaults for LLM parameters: a base set plus optional per-group overrides, each
/// field overridable from configuration and therefore from the environment
/// (<c>LlmDefaults__Temperature</c>, <c>LlmDefaults__Translation__Temperature</c>).
///
/// The shipped values reproduce the request bodies that were hardcoded before 0.221.0, which is what makes
/// the move to platform-managed parameters behaviour-preserving on an empty database.</summary>
public class LlmDefaultsOptions
{
    public const string Section = "LlmDefaults";

    public double? Temperature { get; set; } = 0.3;
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public string? ReasoningEffort { get; set; } = "medium";
    public bool? ReasoningEnabled { get; set; } = false;
    public int? TimeoutSeconds { get; set; } = LlmParameters.DefaultTimeoutSeconds;
    public bool? ToolsSupported { get; set; } = true;
    public bool? ImagesSupported { get; set; } = false;

    /// <summary>Today's one deliberate exception: translation runs cooler than everything else.</summary>
    public LlmParameterDefaults Translation { get; set; } = new() { Temperature = 0.1 };

    public LlmParameterDefaults Tags { get; set; } = new();
    public LlmParameterDefaults Actions { get; set; } = new();
    public LlmParameterDefaults Summaries { get; set; } = new();
    public LlmParameterDefaults MinutesAndFormulas { get; set; } = new();
    public LlmParameterDefaults Chat { get; set; } = new();

    /// <summary>The bottom layer: what applies when neither the model nor its group override says anything.</summary>
    public string? BaseLayer => new LlmParameterDefaults
    {
        Temperature = Temperature,
        TopP = TopP,
        TopK = TopK,
        RepeatPenalty = RepeatPenalty,
        FrequencyPenalty = FrequencyPenalty,
        PresencePenalty = PresencePenalty,
        MaxTokens = MaxTokens,
        MaxCompletionTokens = MaxCompletionTokens,
        ReasoningEffort = ReasoningEffort,
        ReasoningEnabled = ReasoningEnabled,
        TimeoutSeconds = TimeoutSeconds,
        ToolsSupported = ToolsSupported,
        ImagesSupported = ImagesSupported,
    }.ToLayer();

    /// <summary>The group's own default layer, or null when nothing is configured for it. ModelBase and a
    /// null group have no app defaults of their own - they resolve through <see cref="BaseLayer"/>.</summary>
    public string? LayerFor(LlmCallGroup? group) => group switch
    {
        LlmCallGroup.Tags => Tags.ToLayer(),
        LlmCallGroup.Actions => Actions.ToLayer(),
        LlmCallGroup.Summaries => Summaries.ToLayer(),
        LlmCallGroup.MinutesAndFormulas => MinutesAndFormulas.ToLayer(),
        LlmCallGroup.Translation => Translation.ToLayer(),
        LlmCallGroup.Chat => Chat.ToLayer(),
        _ => null,
    };
}
