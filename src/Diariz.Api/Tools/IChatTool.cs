using System.Text.Json;

namespace Diariz.Api.Tools;

/// <summary>The per-request context a tool runs in: the owning user and the recordings currently selected as
/// chat context (used when the model asks a tool to search <c>scope:"current"</c>).</summary>
public sealed record ChatToolContext(Guid UserId, IReadOnlyList<Guid> SelectedRecordingIds);

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
