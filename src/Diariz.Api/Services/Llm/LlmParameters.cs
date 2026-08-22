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

    // ---- OCR: govern the client, never serialised as request keys ----

    /// <summary>What olmOCR-2 responds to, and therefore the shipped default, since it is the default OCR
    /// model. GLM-OCR's row overrides it with the terse "Text Recognition:".</summary>
    public const string DefaultOcrPrompt =
        "Below is the image of one page of a document. Just return the plain text representation of this " +
        "document as if you were reading it naturally. Do not hallucinate.";

    /// <summary>olmOCR-2's measured peak. It is a floor-and-ceiling question, not a maximum: olmOCR reads a
    /// dense capture correctly at 2048 and <i>degrades</i> at 2560, where it starts replacing numbers with
    /// image placeholders.</summary>
    public const int DefaultOcrMaxEdge = 2048;

    /// <summary>The instruction sent alongside the image. Free text, and per model for a measured reason:
    /// GLM-OCR wants "Text Recognition:", olmOCR wants a sentence, and asking either for the other's prompt
    /// measurably changes what comes back - "OCR markdown" narrowed one model to a single table and
    /// discarded the rest of the capture.</summary>
    public string OcrPrompt { get; init; } = DefaultOcrPrompt;

    /// <summary>Longest edge, in pixels, an OCR image may have before it is rescaled. Per model because
    /// four models measured against one capture wanted four different answers, and because more is not
    /// better: quality is non-monotonic in resolution for every model tested.</summary>
    public int OcrMaxEdge { get; init; } = DefaultOcrMaxEdge;
}
