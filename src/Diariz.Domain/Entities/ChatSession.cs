namespace Diariz.Domain.Entities;

/// <summary>A saved chat conversation, scoped to a user. The whole thread and the context it was
/// started with are stored as JSON blobs so the server stays stateless between turns: each request
/// resends the full history + context, and a save simply persists that snapshot for later reload.</summary>
public class ChatSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Short title (LLM-generated on save, falling back to the first user message).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>JSON array of <c>{ role, content }</c> turns.</summary>
    public string MessagesJson { get; set; } = "[]";

    /// <summary>JSON object describing the context: <c>{ recordingIds, attachmentName?, attachmentText? }</c>.</summary>
    public string ContextJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
