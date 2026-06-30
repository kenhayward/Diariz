using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/user/settings")]
public class UserSettingsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IApiKeyProtector _protector;
    private readonly SummarizationOptions _serverDefaults;
    private readonly ChatOptions _chatDefaults;
    private readonly IChatToolSettingsResolver _toolSettings;

    public UserSettingsController(
        DiarizDbContext db, IApiKeyProtector protector, IOptions<SummarizationOptions> serverDefaults,
        IOptions<ChatOptions> chatDefaults, IChatToolSettingsResolver toolSettings)
    {
        _db = db;
        _protector = protector;
        _serverDefaults = serverDefaults.Value;
        _chatDefaults = chatDefaults.Value;
        _toolSettings = toolSettings;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<UserSettingsDto> Get()
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        var tools = await _toolSettings.ResolveAsync(UserId);
        return new UserSettingsDto(
            s?.SummaryApiBase, s?.SummaryModel, !string.IsNullOrEmpty(s?.SummaryApiKeyEncrypted),
            DefaultApiBase: NullIfBlank(_serverDefaults.ApiBase),
            DefaultModel: NullIfBlank(_serverDefaults.Model),
            ServerHasApiKey: !string.IsNullOrEmpty(_serverDefaults.ApiKey),
            ContextWindow: s?.ChatContextWindow,
            DefaultContextWindow: _chatDefaults.ContextLength,
            ToolsEnabled: tools.MasterEnabled,
            DefaultToolsEnabled: _chatDefaults.ToolsEnabled,
            Tools: tools.Catalog
                .Select(c => new ChatToolDto(c.Name, c.Title, c.Description, c.Enabled, c.DefaultEnabled))
                .ToList());
    }

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    [HttpPut]
    public async Task<IActionResult> Update(UpdateUserSettingsRequest req)
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }

        s.SummaryApiBase = Blank(req.ApiBase);
        s.SummaryModel = Blank(req.Model);

        // Tri-state key: null leaves it unchanged, empty clears it, anything else replaces it.
        if (req.ApiKey is not null)
            s.SummaryApiKeyEncrypted = req.ApiKey.Length == 0 ? null : _protector.Protect(req.ApiKey);

        // Context window: a positive value sets the override; null/<=0 clears it (server default applies).
        s.ChatContextWindow = req.ContextWindow is > 0 ? req.ContextWindow : null;

        // Tool calling: a value sets the master override; null leaves it unchanged.
        if (req.ToolsEnabled is not null) s.ChatToolsEnabled = req.ToolsEnabled;

        // Per-tool overrides: a map (possibly empty) replaces them; null leaves them unchanged.
        if (req.ToolOverrides is not null)
            s.ChatToolOverridesJson = req.ToolOverrides.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(req.ToolOverrides)
                : null;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
