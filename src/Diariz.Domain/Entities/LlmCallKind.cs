namespace Diariz.Domain.Entities;

/// <summary>What an LLM call was for. Stored as an int, so this is APPEND ONLY - never renumber or
/// reorder, exactly like <see cref="RecordingSource"/>.</summary>
public enum LlmCallKind
{
    Unknown = 0,
    Summarize = 1,
    SectionSummary = 2,
    MeetingMinutes = 3,
    SectionMinutes = 4,
    /// <summary>Reserved; never written. <c>MeetingTypeMinutesGenerator</c> and both its strategies
    /// deliberately push no scope of their own - every call they make belongs to the enclosing
    /// MeetingMinutes/SectionMinutes operation, so it is attributed to that Kind instead.</summary>
    MeetingTypeMinutes = 5,
    ExtractActions = 6,
    Tags = 7,
    Translation = 8,
    Dictation = 9,
    Embedding = 10,
    SearchQuery = 11,
    ChatMessage = 12,
    FormulaRun = 13,
    ChatTitle = 14,

    /// <summary>An administrator's connection test from /admin/llm-models. Logged like any other call - a
    /// test that cost tokens and did not appear in the usage log would be the one call an admin could not
    /// account for - but it is NOT dispatched by the resolver: the group and parameters come from whatever
    /// the admin is editing, unsaved, so it has no group of its own.</summary>
    AdminTest = 15,

    /// <summary>Reading text off one screen capture, from the capture viewer. Its own kind rather than a
    /// reuse of ChatMessage because it is routed to a different model entirely - a purpose-built OCR model,
    /// not a chat model - and because an administrator needs to see what OCR costs on its own.</summary>
    ScreenshotOcr = 16,
}
