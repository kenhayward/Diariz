namespace Diariz.Api.Services.Llm;

/// <summary>Writes the wire parameters of a resolved set into an OpenAI-compatible request body.
///
/// One place, so every client sends the same shape and adding a parameter later is a single edit rather
/// than seven.
///
/// A null parameter is OMITTED - not written as a JSON null - because a server that validates types
/// rejects <c>"top_k": null</c> with a 400, and "do not send this" is exactly what a null here means.</summary>
public static class LlmRequestBody
{
    public static void Apply(IDictionary<string, object?> body, LlmParameters p)
    {
        Set(body, LlmParameterLayers.Temperature, p.Temperature);
        Set(body, LlmParameterLayers.TopP, p.TopP);
        Set(body, LlmParameterLayers.TopK, p.TopK);
        Set(body, LlmParameterLayers.RepeatPenalty, p.RepeatPenalty);
        Set(body, LlmParameterLayers.FrequencyPenalty, p.FrequencyPenalty);
        Set(body, LlmParameterLayers.PresencePenalty, p.PresencePenalty);
        Set(body, LlmParameterLayers.MaxTokens, p.MaxTokens);
        Set(body, LlmParameterLayers.MaxCompletionTokens, p.MaxCompletionTokens);

        // Gated on the flag so a non-reasoning endpoint never sees the field at all.
        if (p.ReasoningEnabled && p.ReasoningEffort is not null)
            body[LlmParameterLayers.ReasoningEffort] = p.ReasoningEffort;
    }

    private static void Set(IDictionary<string, object?> body, string key, object? value)
    {
        if (value is not null) body[key] = value;
    }
}
