namespace Diariz.Api.Services;

/// <summary>
/// Approximate token accounting for the chat context dial. Uses the common ~4-chars-per-token
/// heuristic — it is indicative, not a real tokenizer, and is only used to drive the UI gauge.
/// </summary>
public static class ChatContextMeter
{
    /// <summary>Ceiling of chars/4 (so any non-empty text is at least one token).</summary>
    public static int EstimateFromChars(long chars) => chars <= 0 ? 0 : (int)((chars + 3) / 4);

    public static int EstimateTokens(string? text) => EstimateFromChars(text?.Length ?? 0);
}
