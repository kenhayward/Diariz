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

    /// <summary>Pixels per token in the common tile-based approximation vision endpoints use.</summary>
    private const int PixelsPerToken = 750;

    /// <summary>Ceiling of pixels/750 for one attached image, measured on the dimensions ACTUALLY sent
    /// (post-rescale), not the stored capture.
    ///
    /// <para>Without this an attached screenshot is invisible to the dial: a turn carrying a full
    /// 1920x1080 capture reads as nearly empty while costing ~2,800 tokens. Since attachments are uncapped,
    /// the dial is the only signal a user gets that a tray has grown expensive - a gauge that is
    /// confidently wrong is worse than one that admits to being approximate.</para></summary>
    public static int EstimateImageTokens(int width, int height) =>
        width <= 0 || height <= 0 ? 0 : (int)(((long)width * height + PixelsPerToken - 1) / PixelsPerToken);
}
