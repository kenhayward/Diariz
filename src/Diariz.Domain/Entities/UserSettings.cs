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
}
