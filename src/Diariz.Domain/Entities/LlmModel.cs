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

    /// <summary>A user-facing name for this model, e.g. "QWEN 3.8". Null or blank means "use the slug" -
    /// see <see cref="Label"/>. Nullable rather than defaulted to a copy of the slug, so that renaming the
    /// model moves the label with it instead of stranding the old slug as a label nobody set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>A short phrase shown beside the name in the chat model picker, e.g. "Use this for most
    /// chats". Null means the row simply has no description - never a generated one: a sentence nobody
    /// wrote would read as advice the platform is giving, which is exactly what it is not.</summary>
    public string? Description { get; set; }

    /// <summary>Whether this model is offered in the chat model picker. The model assigned to
    /// <see cref="LlmCallGroup.Chat"/> is offered whether or not this is set (see <c>ChatModelCatalog</c>),
    /// so an administrator cannot produce an empty picker, or one that excludes the model actually in
    /// use.</summary>
    public bool ChatEnabled { get; set; }

    /// <summary>What a human reads. Never stored: deriving it means a slug rename cannot leave a stale
    /// label behind.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

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
