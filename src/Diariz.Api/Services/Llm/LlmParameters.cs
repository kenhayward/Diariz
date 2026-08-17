namespace Diariz.Api.Services.Llm;

/// <summary>A fully-resolved parameter set: every layer has been walked and every question answered.
///
/// A null on a WIRE parameter means "do not put this key in the request body" - it is not a missing value
/// waiting to be filled in. The BEHAVIOUR flags are never null because they govern the client rather than
/// the body, so they always need an answer.</summary>
public sealed record LlmParameters
{
    public const int DefaultTimeoutSeconds = 120;

    // ---- wire parameters: null means the key is absent from the request body ----

    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public double? RepeatPenalty { get; init; }
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxCompletionTokens { get; init; }

    /// <summary>Free text, not an enum: gpt-oss takes low/medium/high, qwen3 also takes xhigh, and the next
    /// model will take something else. Only reaches the wire when <see cref="ReasoningEnabled"/> is true.</summary>
    public string? ReasoningEffort { get; init; }

    // ---- behaviour flags: never serialised into a body, always decided ----

    /// <summary>When false the reasoning_effort field is omitted entirely, so a non-reasoning endpoint
    /// never sees it.</summary>
    public bool ReasoningEnabled { get; init; }

    /// <summary>The request deadline. The HTTP clients have no cap of their own, so this is the only
    /// authority.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>When false, tools and tool_choice are omitted even if the caller passed tools - a model
    /// that cannot do tool calling should not be asked to.</summary>
    public bool ToolsSupported { get; init; } = true;

    /// <summary>Declared and stored, not yet read by any call site. Present so the schema does not need
    /// revisiting when image input is wired up.</summary>
    public bool ImagesSupported { get; init; }
}
