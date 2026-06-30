using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Tools;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>One tool's resolved state for the settings UI: whether it is on for this user
/// (<paramref name="Enabled"/>, independent of the master switch) and its server-side default.</summary>
public sealed record ChatToolInfo(string Name, string Title, string Description, bool Enabled, bool DefaultEnabled);

/// <summary>The effective chat-tool configuration for a user: the master switch, the tools that are actually
/// active (master AND per-tool enabled), and the full catalog for the settings panel.</summary>
public sealed record ChatToolSettings(
    bool MasterEnabled, IReadOnlyList<IChatTool> ActiveTools, IReadOnlyList<ChatToolInfo> Catalog);

public interface IChatToolSettingsResolver
{
    Task<ChatToolSettings> ResolveAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Resolves chat-tool settings: a master switch (user override ?? server default) and, per tool,
/// a user override ?? server default (a tool is on by default unless named in <c>Chat:DisabledTools</c>).
/// The resolution rules are pure/static so they can be unit-tested without a database.</summary>
public sealed class ChatToolSettingsResolver : IChatToolSettingsResolver
{
    private readonly DiarizDbContext _db;
    private readonly IChatToolRegistry _registry;
    private readonly ChatOptions _opts;

    public ChatToolSettingsResolver(
        DiarizDbContext db, IChatToolRegistry registry, IOptions<ChatOptions> opts)
    {
        _db = db;
        _registry = registry;
        _opts = opts.Value;
    }

    public async Task<ChatToolSettings> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await _db.UserSettings
            .Where(u => u.UserId == userId)
            .Select(u => new { u.ChatToolsEnabled, u.ChatToolOverridesJson })
            .FirstOrDefaultAsync(ct);

        var master = s?.ChatToolsEnabled ?? _opts.ToolsEnabled;
        var disabled = ParseDisabled(_opts.DisabledTools);
        var overrides = ParseOverrides(s?.ChatToolOverridesJson);

        var catalog = _registry.All.Select(t =>
        {
            var def = !disabled.Contains(t.Name);
            var enabled = overrides.TryGetValue(t.Name, out var o) ? o : def;
            return new ChatToolInfo(t.Name, t.Title, t.Description, enabled, def);
        }).ToList();

        var active = master
            ? _registry.All.Where(t => catalog.First(c => c.Name == t.Name).Enabled).ToList()
            : new List<IChatTool>();

        return new ChatToolSettings(master, active, catalog);
    }

    /// <summary>Parses the server CSV of tool names that are off by default.</summary>
    public static HashSet<string> ParseDisabled(string? csv) =>
        new((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

    /// <summary>Parses the user's explicit per-tool override map; invalid/blank JSON yields no overrides.</summary>
    public static IReadOnlyDictionary<string, bool> ParseOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, bool> Empty = new Dictionary<string, bool>();
}
