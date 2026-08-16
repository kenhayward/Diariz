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
}
