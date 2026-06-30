namespace Diariz.Domain.Entities;

/// <summary>Per-user preferences (1:1 with <see cref="ApplicationUser"/>, shared primary key).
/// Currently holds the user's own OpenAI-compatible summarisation config; null fields fall back to
/// the server defaults.</summary>
public class UserSettings
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Base URL of the user's OpenAI-compatible endpoint, e.g. https://api.openai.com/v1.</summary>
    public string? SummaryApiBase { get; set; }

    /// <summary>API key, encrypted at rest (never returned to clients). Null = not set.</summary>
    public string? SummaryApiKeyEncrypted { get; set; }

    public string? SummaryModel { get; set; }

    /// <summary>Chat model's context-window size in tokens, used by the context dial. Null falls back
    /// to the server default (<c>Chat:ContextLength</c>).</summary>
    public int? ChatContextWindow { get; set; }

    /// <summary>Master switch for chat tool calling. Null falls back to the server default
    /// (<c>Chat:ToolsEnabled</c>).</summary>
    public bool? ChatToolsEnabled { get; set; }

    /// <summary>Explicit per-tool on/off overrides, a JSON object of <c>{ "tool_name": bool }</c>. A tool
    /// absent from the map follows the server default, so tools added later default correctly. Null = no
    /// overrides.</summary>
    public string? ChatToolOverridesJson { get; set; }

    /// <summary>The user's native language (BCP-47), used as the default target when translating
    /// transcripts. Null = not set.</summary>
    public string? NativeLanguage { get; set; }

    /// <summary>The language the app UI is shown in (BCP-47). Null = follow the browser / default.</summary>
    public string? UiLanguage { get; set; }
}
