using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly DictationOptions _dictationDefaults;

    public UserSettingsController(
        DiarizDbContext db, IApiKeyProtector protector, IOptions<SummarizationOptions> serverDefaults,
        IOptions<ChatOptions> chatDefaults, IChatToolSettingsResolver toolSettings,
        IOptions<DictationOptions> dictationDefaults)
    {
        _db = db;
        _protector = protector;
        _serverDefaults = serverDefaults.Value;
        _chatDefaults = chatDefaults.Value;
        _toolSettings = toolSettings;
        _dictationDefaults = dictationDefaults.Value;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("Get your AI settings")]
    [EndpointDescription(
        "Your AI configuration: the OpenAI-compatible endpoint and model used for summaries, minutes, " +
        "formulas and chat, the chat context window, reasoning options, which chat tools are on, and where " +
        "new recordings are filed.\n\n" +
        "Each setting comes back **alongside the server default** it falls back to, so a client can show " +
        "\"using the platform default\" instead of an empty box - your value is an override, not a " +
        "requirement.\n\n" +
        "Also included: how a recording started from a calendar event should end - whether it stops by " +
        "itself, how long it keeps going past the invite's end time, and how much silence ends it early; " +
        "and the per-request LLM timeout, alongside the platform/server default it falls back to.\n\n" +
        "**Your API key is never returned.** Only `hasApiKey` says whether one is stored.")]
    public async Task<UserSettingsDto> Get()
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
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
                .ToList(),
            // Reasoning: effective (user override ?? server default) + the server default for the placeholder.
            ReasoningEnabled: s?.ReasoningEnabled ?? _serverDefaults.ReasoningEnabled,
            ReasoningEffort: NullIfBlank(s?.ReasoningEffort) ?? _serverDefaults.ReasoningEffort,
            DefaultReasoningEnabled: _serverDefaults.ReasoningEnabled,
            DefaultReasoningEffort: _serverDefaults.ReasoningEffort,
            PlacementMode: s?.RecordingPlacementMode ?? RecordingPlacementMode.SelectedFolder,
            PlacementSectionId: s?.RecordingPlacementSectionId,
            DictationServerAvailable: _dictationDefaults.Enabled,
            OutlookSyncEnabled: s?.OutlookSyncEnabled ?? false,
            CalendarAutoStopEnabled: s?.CalendarAutoStopEnabled ?? false,
            CalendarAutoStopAfterMinutes:
                s?.CalendarAutoStopAfterMinutes ?? UserSettings.DefaultCalendarAutoStopAfterMinutes,
            CalendarSilenceStopSeconds:
                s?.CalendarSilenceStopSeconds ?? UserSettings.DefaultCalendarSilenceStopSeconds,
            LlmTimeoutSeconds: s?.LlmTimeoutSeconds,
            DefaultLlmTimeoutSeconds: ps?.LlmTimeoutSeconds ?? _serverDefaults.TimeoutSeconds);
    }

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    [HttpPut]
    [EndpointSummary("Update your AI settings")]
    [EndpointDescription(
        "A **partial update** - the opposite of the profile endpoint, which replaces everything. Each field " +
        "is three-way: **omit it** (or send null) to leave it alone, send **empty** to clear your override " +
        "and fall back to the platform default, or send a value to set one. That is what lets separate " +
        "settings tabs save independently without wiping each other's fields.\n\n" +
        "The API key follows the same rule and is **write-only**: send it to replace, send empty to remove, " +
        "omit to keep. It is encrypted at rest and never returned.\n\n" +
        "For the context window a value of zero or less clears the override rather than setting it. Choosing " +
        "a placement mode other than a specific folder clears the stored folder, so a stale one cannot " +
        "resurface if you switch back.\n\n" +
        "The two calendar-recording durations behave differently again: a value of zero or less **resets " +
        "them to the default** rather than clearing or storing it, since a zero would stop a recording the " +
        "moment it started.\n\n" +
        "`outlookSyncEnabled` is the one field with a side effect. Setting it **false erases**: every connected " +
        "device is removed along with every meeting mirrored from it. It is a privacy switch, so it clears what " +
        "was gathered rather than only stopping future syncs - omit the field if you just want to leave it as " +
        "it is.\n\n" +
        "`llmTimeoutSeconds` follows its own rule: null leaves it unchanged, 0 clears the override and falls " +
        "back to the platform/server default, and a value of 5 or more sets it. A value of 1-4 seconds is " +
        "rejected with a 400 rather than silently coerced, since that is not a working timeout.")]
    public async Task<IActionResult> Update(UpdateUserSettingsRequest req)
    {
        // Validated first, before touching `s` or the change tracker at all: the method assigns fields as
        // it goes (one of them, OutlookSyncEnabled: false, queues a RemoveRange) and only calls
        // SaveChangesAsync once at the end, so returning BadRequest never persists a partial write either
        // way - but validating up front keeps every branch below this point simple ("we already know the
        // value is usable") instead of each one having to guard against a value it might still reject.
        if (req.LlmTimeoutSeconds is > 0 and < 5)
            return BadRequest("An LLM timeout must be at least 5 seconds.");

        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }

        // Text fields are tri-state: null leaves them unchanged (a settings tab that doesn't own the field
        // omits it), "" (or whitespace) clears the override, a value sets it. The personal settings tabs now
        // save independently, so a partial PUT must never wipe another tab's fields.
        if (req.ApiBase is not null) s.SummaryApiBase = Blank(req.ApiBase);
        if (req.Model is not null) s.SummaryModel = Blank(req.Model);

        // Tri-state key: null leaves it unchanged, empty clears it, anything else replaces it.
        if (req.ApiKey is not null)
            s.SummaryApiKeyEncrypted = req.ApiKey.Length == 0 ? null : _protector.Protect(req.ApiKey);

        // Context window: null leaves it unchanged; a positive value sets the override; <=0 clears it.
        if (req.ContextWindow is not null)
            s.ChatContextWindow = req.ContextWindow > 0 ? req.ContextWindow : null;

        // Tool calling: a value sets the master override; null leaves it unchanged.
        if (req.ToolsEnabled is not null) s.ChatToolsEnabled = req.ToolsEnabled;

        // Per-tool overrides: a map (possibly empty) replaces them; null leaves them unchanged.
        if (req.ToolOverrides is not null)
            s.ChatToolOverridesJson = req.ToolOverrides.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(req.ToolOverrides)
                : null;

        // Reasoning: a value sets the master override (null leaves it unchanged); a blank effort clears the
        // per-user level (server default applies).
        if (req.ReasoningEnabled is not null) s.ReasoningEnabled = req.ReasoningEnabled;
        if (req.ReasoningEffort is not null) s.ReasoningEffort = Blank(req.ReasoningEffort);

        // Placement: a mode replaces the preference; null leaves it unchanged. The fixed folder only applies in
        // SpecificFolder mode (cleared otherwise, so a stale id can't resurface if the user flips back).
        if (req.PlacementMode is { } mode)
        {
            s.RecordingPlacementMode = mode;
            s.RecordingPlacementSectionId =
                mode == RecordingPlacementMode.SpecificFolder ? req.PlacementSectionId : null;
        }

        // The desktop Outlook opt-in. Turning it OFF erases: a privacy switch that left the meeting bodies and
        // attendee addresses it had already gathered sitting on the server would not be one, and this is what
        // the confirm dialog in Preferences promises. Removing the sources cascades their events away.
        if (req.OutlookSyncEnabled is { } outlook)
        {
            s.OutlookSyncEnabled = outlook;
            if (!outlook)
            {
                var devices = await _db.OutlookCalendarSources.Where(x => x.UserId == UserId).ToListAsync();
                _db.OutlookCalendarSources.RemoveRange(devices);
            }
        }

        // Recording from a calendar event. The two durations are clamped rather than trusted: a zero or
        // negative wait would stop the take the instant it began, and a zero silence window would end it
        // before anyone spoke, so a nonsensical value resets to the default instead of persisting a trap.
        if (req.CalendarAutoStopEnabled is { } calendarAutoStop) s.CalendarAutoStopEnabled = calendarAutoStop;
        if (req.CalendarAutoStopAfterMinutes is { } after)
            s.CalendarAutoStopAfterMinutes =
                after > 0 ? after : UserSettings.DefaultCalendarAutoStopAfterMinutes;
        if (req.CalendarSilenceStopSeconds is { } silence)
            s.CalendarSilenceStopSeconds =
                silence > 0 ? silence : UserSettings.DefaultCalendarSilenceStopSeconds;

        // Timeout: null leaves it unchanged; 0 clears the override; >=5 sets it (validated at the top of the
        // method - the floor mirrors the admin field, since 1-4s is not a working timeout and coercing it
        // silently would hide the mistake).
        if (req.LlmTimeoutSeconds is not null)
            s.LlmTimeoutSeconds = req.LlmTimeoutSeconds > 0 ? req.LlmTimeoutSeconds : null;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
