using System.Security.Claims;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Diariz.Api.Mcp;

/// <summary>Low-level MCP request handlers that project Diariz's chat tool registry onto the MCP endpoint.
/// The MCP endpoint runs inside the normal HTTP pipeline, so the authenticated user and the request's scoped
/// services are read from <see cref="IHttpContextAccessor"/> (the SDK's <c>RequestContext</c> does not expose a
/// service scope). All data access stays owner-scoped via the user's <see cref="ClaimTypes.NameIdentifier"/>,
/// exactly like the rest of the API.</summary>
public sealed class DiarizMcpHandlers
{
    private readonly IHttpContextAccessor _http;

    public DiarizMcpHandlers(IHttpContextAccessor http) => _http = http;

    private (Guid UserId, HttpContext Http, IServiceProvider Services) Resolve()
    {
        var http = _http.HttpContext
            ?? throw new InvalidOperationException("MCP handler invoked outside an HTTP request.");
        var idText = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("MCP request is not authenticated.");
        return (Guid.Parse(idText), http, http.RequestServices);
    }

    /// <summary>The user's exposable tools: the per-tool-enabled chat tools (independent of the chat master
    /// switch — MCP's opt-in is holding a token), minus the ones that only work with an in-chat selection.</summary>
    private static async Task<IReadOnlyList<IChatTool>> ExposedToolsAsync(
        Guid userId, IServiceProvider sp, CancellationToken ct)
    {
        var settings = await sp.GetRequiredService<IChatToolSettingsResolver>().ResolveAsync(userId, ct);
        var enabled = settings.Catalog.Where(c => c.Enabled).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var registry = sp.GetRequiredService<IChatToolRegistry>();
        return McpToolProjection.Expose(registry.All.Where(t => enabled.Contains(t.Name)));
    }

    public async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var (userId, _, sp) = Resolve();
        var tools = (await ExposedToolsAsync(userId, sp, ct))
            .Select(t => new Tool
            {
                Name = t.Name,
                Title = t.Title,
                Description = t.Description,
                InputSchema = McpToolProjection.InputSchema(t),
            })
            .ToList();
        return new ListToolsResult { Tools = tools };
    }

    public async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var (userId, http, sp) = Resolve();
        var name = ctx.Params?.Name ?? "";

        var tool = (await ExposedToolsAsync(userId, sp, ct))
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null)
            return Error($"Unknown or disabled tool '{name}'.");

        // The MCP arguments map serialises to the same JSON object shape the chat tools already parse.
        var args = ctx.Params?.Arguments is { } a
            ? JsonSerializer.SerializeToElement(a)
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());

        string result;
        try
        {
            // No in-chat "selected recordings" over MCP; tools that need it are excluded from the catalog.
            result = await tool.ExecuteAsync(args, new ChatToolContext(userId, []), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"The tool '{name}' failed: {ex.Message}");
        }

        // Make the in-app deep-links absolute so they're clickable from Claude.
        result = McpLinkRewriter.Rewrite(result, WebBase(http, sp));
        return new CallToolResult { Content = [new TextContentBlock { Text = result }] };
    }

    public async ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var (userId, _, sp) = Resolve();
        var infos = await sp.GetRequiredService<IMcpResourceService>().ListAsync(userId, ct);
        var resources = infos
            .Select(i => new Resource { Uri = i.Uri, Name = i.Name, Description = i.Description, MimeType = i.MimeType })
            .ToList();
        return new ListResourcesResult { Resources = resources };
    }

    public async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var (userId, _, sp) = Resolve();
        var uri = ctx.Params?.Uri ?? "";
        var content = await sp.GetRequiredService<IMcpResourceService>().ReadAsync(userId, uri, ct)
            ?? throw new InvalidOperationException($"Resource not found: {uri}");
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = content.Uri, MimeType = content.MimeType, Text = content.Text }],
        };
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    /// <summary>Public web origin for absolute links: the configured <c>App:PublicUrl</c>, else the request's
    /// own scheme+host (the SPA is served same-origin with the API).</summary>
    private static string? WebBase(HttpContext http, IServiceProvider sp)
    {
        var configured = sp.GetRequiredService<IOptions<AppPublicOptions>>().Value.PublicUrl;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return http.Request.Host.HasValue ? $"{http.Request.Scheme}://{http.Request.Host}" : null;
    }
}
