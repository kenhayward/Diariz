namespace Diariz.Domain.Entities;

/// <summary>One model the platform can call: its identity, how to reach it, and its context window.
///
/// Self-contained on purpose - pointing a call group at a model brings the connection with it, which is
/// what lets a local LM Studio model and a cloud model coexist on one platform.</summary>
public class LlmModel
{
    public Guid Id { get; set; }

    /// <summary>The literal string sent as <c>model</c> in the request body, e.g. <c>openai/gpt-oss-20b</c>.
    /// Unique.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the OpenAI-compatible endpoint, e.g. <c>http://localhost:1234/v1</c>.</summary>
    public string ApiBase { get; set; } = string.Empty;

    /// <summary>Encrypted at rest via IApiKeyProtector; never returned to clients. Null = no key needed,
    /// which is normal for a local endpoint.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>The model's context window in tokens. A fact about the model, which is why it lives here
    /// rather than in per-user settings where it used to be.</summary>
    public int ContextLength { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<LlmModelParameters> Parameters { get; set; } = new List<LlmModelParameters>();
}
