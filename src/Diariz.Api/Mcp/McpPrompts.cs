namespace Diariz.Api.Mcp;

/// <summary>A prompt argument (name + description + whether it's required).</summary>
public sealed record McpPromptArg(string Name, string Description, bool Required);

/// <summary>A canned MCP prompt: a name, a description, its arguments, and a builder that renders the user
/// message from the supplied argument values.</summary>
public sealed record McpPromptDef(
    string Name, string Description, IReadOnlyList<McpPromptArg> Arguments, Func<Func<string, string?>, string> Render);

/// <summary>The catalog of MCP "prompts" — slash-command-style starters surfaced in Claude that expand into a
/// ready-made instruction grounded in the user's meetings (the model then calls the transcript tools to answer).
/// Pure/static and free of the SDK + database, so the catalog and rendering are unit-testable; the argument
/// values are supplied through a <c>Func</c> accessor so this stays decoupled from the MCP argument type.</summary>
public static class McpPrompts
{
    public static readonly IReadOnlyList<McpPromptDef> All =
    [
        new("summarise_last_meeting",
            "Summarise your most recent meeting.",
            [],
            _ =>
                "Find my most recent recording (use the list_recordings tool, newest first) and give me a " +
                "concise summary — the key points, decisions, and any action items. Use get_recording_summary " +
                "or get_transcript as needed, and link back to the recording."),

        new("open_action_items",
            "List your open action items across all meetings.",
            [],
            _ =>
                "List my outstanding action items across all my meetings (use the list_action_items tool). " +
                "Group them by person where possible, include any deadlines, link each back to the meeting it " +
                "came from, and skip items already completed."),

        new("find_discussion",
            "Find where a topic was discussed across your meetings.",
            [new McpPromptArg("topic", "The topic, phrase, or keywords to look for.", Required: true)],
            arg =>
            {
                var topic = arg("topic");
                if (string.IsNullOrWhiteSpace(topic))
                    throw new ArgumentException("The 'topic' argument is required.");
                return $"Search my transcripts for \"{topic.Trim()}\" (use the search_transcripts tool across " +
                    "all recordings). Summarise what was said, by whom and when, and link back to each moment " +
                    "in the transcript.";
            }),
    ];

    public static McpPromptDef? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Renders a prompt's user message. Returns null for an unknown name; throws
    /// <see cref="ArgumentException"/> when a required argument is missing.</summary>
    public static string? Render(string? name, Func<string, string?> arg)
    {
        var def = Find(name);
        return def?.Render(arg);
    }
}
