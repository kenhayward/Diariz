using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diariz.Api.Tools;

namespace Diariz.Api.Services;

/// <summary>An event surfaced to the chat client while a turn runs.</summary>
public abstract record ChatEvent;

/// <summary>A streamed slice of the assistant's text answer.</summary>
public sealed record ChatTokenEvent(string Value) : ChatEvent;

/// <summary>A tool call has started executing (drives the ephemeral "Tool call: …" indicator).</summary>
public sealed record ChatToolStartEvent(string Name) : ChatEvent;

/// <summary>A tool call has finished executing.</summary>
public sealed record ChatToolEndEvent(string Name) : ChatEvent;

/// <summary>A recording a tool referenced — surfaced so the client can linkify plain mentions of that
/// recording in the assistant's answer even when the model didn't keep the markdown link.</summary>
public sealed record ChatRefEvent(string Name, string Href) : ChatEvent;

public interface IChatToolOrchestrator
{
    /// <summary>Runs a chat turn, looping over tool calls until the model produces a text answer (or the
    /// round cap is hit). Yields token/tool events in order. When <paramref name="tools"/> is empty this is a
    /// single plain streaming turn — identical to the no-tools chat path.</summary>
    IAsyncEnumerable<ChatEvent> RunAsync(
        SummarizationRequestConfig cfg, IReadOnlyList<ChatMessage> seed,
        IReadOnlyList<IChatTool> tools, ChatToolContext ctx, CancellationToken ct = default);
}

/// <summary>Drives the OpenAI tool-calling loop: call the model with the tool specs, execute any tool calls
/// server-side, re-inject their results as <c>tool</c> messages, and repeat until the model answers in text.
/// Bounded by <see cref="MaxToolRounds"/>; the final round offers no tools so the model must answer.</summary>
public sealed class ChatToolOrchestrator : IChatToolOrchestrator
{
    /// <summary>Maximum model round-trips for one user turn (a backstop against tool-call loops).</summary>
    public const int MaxToolRounds = 5;

    private readonly IChatStreamClient _chat;

    public ChatToolOrchestrator(IChatStreamClient chat) => _chat = chat;

    public async IAsyncEnumerable<ChatEvent> RunAsync(
        SummarizationRequestConfig cfg, IReadOnlyList<ChatMessage> seed,
        IReadOnlyList<IChatTool> tools, ChatToolContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = seed.Select(m => (object)new { role = m.Role, content = m.Content }).ToList();
        var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolSpecs = tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
        }).ToList();

        for (var round = 1; round <= MaxToolRounds; round++)
        {
            // Offer tools on every round except the last (which forces a text answer).
            var offer = tools.Count > 0 && round < MaxToolRounds ? toolSpecs : null;
            var acc = new ToolCallAccumulator();

            await foreach (var delta in _chat.StreamChunksAsync(cfg, messages, offer, ct))
            {
                if (!string.IsNullOrEmpty(delta.Content))
                    yield return new ChatTokenEvent(delta.Content!);
                acc.Add(delta.ToolCalls);
            }

            if (!acc.HasCalls) yield break; // model answered in text — done

            var calls = acc.Build();
            messages.Add(new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = calls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.Arguments },
                }).ToArray(),
            });

            foreach (var call in calls)
            {
                yield return new ChatToolStartEvent(call.Name);
                var result = await ExecuteAsync(tools, call, ctx, ct);
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = result });
                // Surface the recordings this result referenced (once each) so the client can linkify them.
                foreach (var (name, href) in ExtractRecordingRefs(result))
                    if (seenRefs.Add(name))
                        yield return new ChatRefEvent(name, href);
                yield return new ChatToolEndEvent(call.Name);
            }
        }
    }

    private static readonly Regex RecordingLinkRegex =
        new(@"\[([^\]]+)\]\((/recordings/[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>Pulls (recordingName, recordingHref) pairs out of a tool result's markdown links. The name has
    /// any trailing " @ mm:ss" stripped and the href is reduced to the whole-recording path.</summary>
    public static IReadOnlyList<(string Name, string Href)> ExtractRecordingRefs(string toolResult)
    {
        var refs = new List<(string, string)>();
        if (string.IsNullOrEmpty(toolResult)) return refs;
        foreach (Match m in RecordingLinkRegex.Matches(toolResult))
        {
            var name = Regex.Replace(m.Groups[1].Value, @"\s+@\s+[\d:]+$", "").Trim();
            var href = m.Groups[2].Value.Split('?')[0];
            if (name.Length > 0) refs.Add((name, href));
        }
        return refs;
    }

    /// <summary>Finds and runs the named tool, returning its text result. Unknown tools and bad argument JSON
    /// become an error string fed back to the model (so it can recover) rather than throwing.</summary>
    private static async Task<string> ExecuteAsync(
        IReadOnlyList<IChatTool> tools, ToolCall call, ChatToolContext ctx, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        if (tool is null) return $"Unknown tool '{call.Name}'.";

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return $"The arguments for '{call.Name}' were not valid JSON.";
        }

        try
        {
            return await tool.ExecuteAsync(args, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"The tool '{call.Name}' failed: {ex.Message}";
        }
    }
}
