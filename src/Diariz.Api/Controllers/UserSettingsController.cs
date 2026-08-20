using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
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
    private readonly ChatOptions _chatDefaults;
    private readonly IChatToolSettingsResolver _toolSettings;
    private readonly IChatContextResolver _contextResolver;
    private readonly ILlmSettingsResolver _llmSettings;
    private readonly DictationOptions _dictationDefaults;

    public UserSettingsController(
        DiarizDbContext db, IOptions<ChatOptions> chatDefaults, IChatToolSettingsResolver toolSettings,
        IChatContextResolver contextResolver, ILlmSettingsResolver llmSettings,
        IOptions<DictationOptions> dictationDefaults)
    {
        _db = db;
        _chatDefaults = chatDefaults.Value;
        _toolSettings = toolSettings;
        _contextResolver = contextResolver;
        _llmSettings = llmSettings;
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
        "`chatModelId` is the model you last chose in the chat picker, and `contextWindow` / `chatModel` " +
        "are derived from it - they describe the model that will actually answer you, not the platform " +
        "default. It comes back null when you have made no choice, or when the model you chose is no " +
        "longer offered for chat.\n\n" +
        "**Your API key is never returned.** Only `hasApiKey` says whether one is stored.")]
    public async Task<UserSettingsDto> Get()
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        var tools = await _toolSettings.ResolveAsync(UserId);
        return new UserSettingsDto(
            // Read-only from 0.221.0: the window belongs to the model the platform assigns to chat, so the
            // dial still has a number to report against but the user has nothing to set.
            ContextWindow: await _contextResolver.ResolveContextWindowAsync(s?.ChatModelId),
            ChatModel: (await _llmSettings.ResolveAsync(LlmCallKind.ChatMessage, s?.ChatModelId)).Model,
            ChatModelId: s?.ChatModelId,
            ToolsEnabled: tools.MasterEnabled,
            DefaultToolsEnabled: _chatDefaults.ToolsEnabled,
            Tools: tools.Catalog
                .Select(c => new ChatToolDto(c.Name, c.Title, c.Description, c.Enabled, c.DefaultEnabled))
                .ToList(),
            PlacementMode: s?.RecordingPlacementMode ?? RecordingPlacementMode.SelectedFolder,
            PlacementSectionId: s?.RecordingPlacementSectionId,
            DictationServerAvailable: _dictationDefaults.Enabled,
            OutlookSyncEnabled: s?.OutlookSyncEnabled ?? false,
            CalendarAutoStopEnabled: s?.CalendarAutoStopEnabled ?? false,
            CalendarAutoStopAfterMinutes:
                s?.CalendarAutoStopAfterMinutes ?? UserSettings.DefaultCalendarAutoStopAfterMinutes,
            CalendarSilenceStopSeconds:
                s?.CalendarSilenceStopSeconds ?? UserSettings.DefaultCalendarSilenceStopSeconds,
            AutoMergeSpeakerSegments: s?.AutoMergeSpeakerSegments ?? false);
    }

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
        "rejected with a 400 rather than silently coerced, since that is not a working timeout.\n\n" +
        "`chatModelId` picks which model answers your chat, from `GET /api/chat/models`. Null leaves it " +
        "unchanged, an all-zero GUID clears it and follows the platform's choice, and a value sets it. An " +
        "id that is not offered is stored but ignored, so an administrator un-ticking a model does not " +
        "destroy your choice.")]
    public async Task<IActionResult> Update(UpdateUserSettingsRequest req)
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }

        // Tool calling: a value sets the master override; null leaves it unchanged.
        if (req.ToolsEnabled is not null) s.ChatToolsEnabled = req.ToolsEnabled;

        // Chat model: Guid.Empty clears, a value sets, null leaves alone - the same three-way shape the
        // numeric fields use. Not checked against the offered set: an un-ticked model must stay stored so
        // re-ticking restores the choice.
        if (req.ChatModelId is { } chatModelId)
            s.ChatModelId = chatModelId == Guid.Empty ? null : chatModelId;

        // Per-tool overrides: a map (possibly empty) replaces them; null leaves them unchanged.
        if (req.ToolOverrides is not null)
            s.ChatToolOverridesJson = req.ToolOverrides.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(req.ToolOverrides)
                : null;

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

        // No clamping: it is a plain switch, unlike the two durations above.
        if (req.AutoMergeSpeakerSegments is { } autoMerge) s.AutoMergeSpeakerSegments = autoMerge;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
