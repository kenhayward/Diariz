namespace Diariz.Api.Services;

/// <summary>The single context budget for every LLM call in the platform, derived from the configured model
/// context window (<c>Chat:ContextLength</c>, per-user overridable via <c>UserSettings.ChatContextWindow</c>).
///
/// <para>Each prompt builder used to carry its own hard-coded constant - 24,000 chars for a folder summary,
/// 32,000 for folder minutes, 16,000 for the whole minutes context, 48,000 for chat - none of which had any
/// relationship to the model actually being called. On a 131k-token endpoint that meant folder roll-ups were
/// silently dropping meetings (<see cref="FolderSummaryPrompt.JoinItems"/> omits whole items once the budget
/// is spent) while using under 5% of the available window. One number now governs them all.</para>
///
/// <para>The budget is deliberately a <b>fraction</b> of the window rather than the whole thing: the injected
/// context shares the window with the instruction template, the chat history, and - critically - the model's
/// own completion, which is charged against the same window. <see cref="InputShare"/> leaves the rest.</para></summary>
public static class LlmContextBudget
{
    /// <summary>Share of the context window available for injected context. The remaining 40% covers the
    /// system template, chat history, and the completion (a folder minutes document can run to thousands of
    /// tokens). Not configurable: the knob users turn is the window itself.</summary>
    public const double InputShare = 0.60;

    /// <summary>The ~4-chars-per-token heuristic, matching <see cref="ChatContextMeter"/> so the UI dial and
    /// the actual truncation agree. Indicative, not a real tokenizer - hence the conservative share above.</summary>
    public const int CharsPerToken = 4;

    /// <summary>Floor applied to the result. A window that is unset, zero, or nonsensically small must still
    /// yield a usable prompt: a zero budget would hand the model an empty transcript rather than a short one.
    /// 24,000 chars is what the per-recording paths used before this type existed, so the floor can never be
    /// a regression on the old behaviour.</summary>
    public const int MinimumChars = 24_000;

    /// <summary>Characters of context to send for a model whose window is <paramref name="contextWindowTokens"/>
    /// tokens. Computed in <c>long</c> so a very large window cannot overflow the multiplication.</summary>
    public static int CharsFor(int contextWindowTokens)
    {
        if (contextWindowTokens <= 0) return MinimumChars;
        var chars = (long)(contextWindowTokens * InputShare) * CharsPerToken;
        return chars <= MinimumChars ? MinimumChars : (int)Math.Min(chars, int.MaxValue);
    }
}
