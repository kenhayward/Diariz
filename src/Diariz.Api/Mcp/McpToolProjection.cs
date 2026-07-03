using System.Text.Json;
using Diariz.Api.Tools;

namespace Diariz.Api.Mcp;

/// <summary>Projects the chat <see cref="IChatTool"/> registry onto the MCP tool surface. The MCP endpoint has
/// no in-chat "selected recordings" context, so tools that depend on it (currently only
/// <c>add_as_attachment</c>) are excluded; every other read/search tool and <c>send_email</c> is exposed
/// verbatim, its <see cref="IChatTool.ParametersSchema"/> becoming the MCP tool's <c>inputSchema</c>.</summary>
public static class McpToolProjection
{
    /// <summary>Chat tools not exposed over MCP because they only work with an in-chat selection context.</summary>
    public static readonly IReadOnlySet<string> ExcludedToolNames =
        new HashSet<string>(StringComparer.Ordinal) { "add_as_attachment" };

    /// <summary>Filters the active chat tools down to those exposable over MCP, preserving order.</summary>
    public static IReadOnlyList<IChatTool> Expose(IEnumerable<IChatTool> tools) =>
        tools.Where(t => !ExcludedToolNames.Contains(t.Name)).ToList();

    /// <summary>Serialises a tool's parameter schema to the JSON element MCP wants for <c>inputSchema</c>.</summary>
    public static JsonElement InputSchema(IChatTool tool) =>
        JsonSerializer.SerializeToElement(tool.ParametersSchema);
}
