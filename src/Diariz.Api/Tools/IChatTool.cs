using System.Text.Json;

namespace Diariz.Api.Tools;

/// <summary>One of the chat-context recordings a draft attachment could be saved to (id + display title).</summary>
public sealed record DraftRecording(Guid Id, string Title);

/// <summary>A note a tool prepared to be saved to a transcript as a Markdown attachment, with the candidate
/// recordings (the selected ones). The client resolves the destination — one → add it; several → let the user
/// pick.</summary>
public sealed record AttachmentDraft(string Name, string Content, IReadOnlyList<DraftRecording> Recordings);

/// <summary>A side-channel a tool can use to surface client-side actions from inside <c>ExecuteAsync</c> (the
/// orchestrator drains it after the tool runs and emits the matching stream events). Currently just pending
/// attachment drafts.</summary>
public sealed class ChatToolEffects
{
    public List<AttachmentDraft> AttachmentDrafts { get; } = new();
}

/// <summary>The per-request context a tool runs in: the owning user, the recordings currently selected as
/// chat context (used when the model asks a tool to search <c>scope:"current"</c>), and an optional
/// <see cref="ChatToolEffects"/> sink for tools that drive the client (the orchestrator supplies it).</summary>
public sealed record ChatToolContext(
    Guid UserId, IReadOnlyList<Guid> SelectedRecordingIds, ChatToolEffects? Effects = null);

/// <summary>A built-in chat tool the LLM can call. Implementations are stateless and resolve their data
/// dependencies (e.g. <see cref="Services.ITranscriptSearch"/>) via DI; all data access is scoped to
/// <see cref="ChatToolContext.UserId"/>.</summary>
public interface IChatTool
{
    /// <summary>Stable snake_case identifier sent to the model and stored in settings, e.g. "who_said_that".</summary>
    string Name { get; }

    /// <summary>Human-friendly label for the settings UI.</summary>
    string Title { get; }

    /// <summary>One-line capability description shown to the model in the tool spec.</summary>
    string Description { get; }

    /// <summary>JSON-Schema object for the tool's <c>function.parameters</c>.</summary>
    object ParametersSchema { get; }

    /// <summary>Runs the tool and returns the text result that is fed back to the model as a tool message.</summary>
    Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct);
}

/// <summary>Collects all registered <see cref="IChatTool"/>s.</summary>
public interface IChatToolRegistry
{
    IReadOnlyList<IChatTool> All { get; }
    IChatTool? Find(string name);
}

public sealed class ChatToolRegistry : IChatToolRegistry
{
    public ChatToolRegistry(IEnumerable<IChatTool> tools) => All = tools.ToList();

    public IReadOnlyList<IChatTool> All { get; }

    public IChatTool? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
