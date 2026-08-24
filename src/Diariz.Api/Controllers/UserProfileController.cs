using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Localization;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The signed-in user's own profile: display name + language preferences. Updating returns a
/// fresh access token so the new display name takes effect immediately (it lives in the JWT name claim).</summary>
[ApiController]
[Authorize]
[Route("api/user/profile")]
public class UserProfileController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly DiarizDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IPlatformSettingsService _platform;
    private readonly IUserPermissions _permissions;
    private readonly IPeopleDirectory _people;
    private readonly IRoomScope _rooms;

    public UserProfileController(
        UserManager<ApplicationUser> users, DiarizDbContext db, ITokenService tokens,
        IPlatformSettingsService platform, IUserPermissions permissions, IPeopleDirectory people,
        IRoomScope rooms)
    {
        _users = users;
        _db = db;
        _tokens = tokens;
        _platform = platform;
        _permissions = permissions;
        _people = people;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static PermissionsDto ToDto(PlatformPermission p) => new(
        p.HasFlag(PlatformPermission.ManageRooms),
        p.HasFlag(PlatformPermission.ManageUsers),
        p.HasFlag(PlatformPermission.ManagePlatform),
        p.HasFlag(PlatformPermission.ManageFormulas),
        p.HasFlag(PlatformPermission.ManagePeople));

    [HttpGet]
    [EndpointSummary("Get your profile")]
    [EndpointDescription(
        "Who you are and how the app is set up for you: email, display name, interface and native languages, " +
        "theme, the professional details that give the model context about your work, and whether Google " +
        "sign-in and Calendar are connected.\n\n" +
        "It also reports your **platform permissions** and which optional features the administrator has " +
        "enabled (API access, MCP access, Automations) - so a client can hide what you cannot use rather " +
        "than discovering it through a 403.")]
    public async Task<ActionResult<UserProfileDto>> Get()
    {
        var user = await _users.FindByIdAsync(UserId.ToString());
        if (user is null) return NotFound();
        var s = await _db.UserSettings.FindAsync(UserId);
        var settings = await _platform.GetAsync();
        var apiAccessEnabled = settings.ApiAccessEnabled;
        var webhooksEnabled = settings.WebhooksEnabled;
        var mcpAccessEnabled = settings.McpAccessEnabled;
        return new UserProfileDto(user.Email ?? "", user.FullName, s?.NativeLanguage, s?.UiLanguage,
            GoogleConnected: user.GoogleSubject is not null,
            GoogleCalendar: s?.GoogleCalendarGranted ?? false,
            JobTitle: s?.JobTitle, CompanyName: s?.CompanyName, JobDescription: s?.JobDescription,
            CompanyDescription: s?.CompanyDescription, LinkedIn: s?.LinkedIn,
            Theme: ThemeToString(s?.Theme ?? ThemePreference.Auto),
            TranscriptionLanguage: s?.TranscriptionLanguage,
            ApiAccessEnabled: apiAccessEnabled,
            WebhooksEnabled: webhooksEnabled,
            McpAccessEnabled: mcpAccessEnabled,
            Permissions: ToDto(await _permissions.ForAsync(UserId)));
    }

    [HttpPut]
    [EndpointSummary("Update your profile")]
    [EndpointDescription(
        "Saves your display name, languages, theme, and professional details. This is a **full replacement, " +
        "not a patch**: every field you omit or send blank is cleared, so read the profile first and send it " +
        "back with your changes.\n\n" +
        "**It returns a new session token**, because your name travels in the token and would otherwise stay " +
        "stale until the next sign-in - replace your stored token with this one. Your email cannot be changed " +
        "here. 400 for a language code the platform does not support.")]
    public async Task<ActionResult<AuthResponse>> Update(UpdateUserProfileRequest req)
    {
        var user = await _users.FindByIdAsync(UserId.ToString());
        if (user is null) return NotFound();

        var native = Blank(req.NativeLanguage);
        var ui = Blank(req.UiLanguage);
        var transcription = Blank(req.TranscriptionLanguage);
        if (native is not null && !SupportedLanguages.IsSupported(native))
            return BadRequest("Unknown native language.");
        if (ui is not null && !SupportedLanguages.IsSupported(ui))
            return BadRequest("Unknown UI language.");
        if (transcription is not null && !SupportedLanguages.IsSupported(transcription))
            return BadRequest("Unknown transcription language.");

        user.FullName = Blank(req.FullName);
        await _users.UpdateAsync(user);

        // A user is also a person, and their name is denormalised onto every speaker they have been
        // identified as - so a rename here has to reach the directory, or past transcripts keep the old one.
        await _people.SyncFromUserAsync(UserId);

        // A personal room is named from the display name when it is created and is immutable to the user, so
        // it has to follow a rename too. It did not, and a renamed account kept showing the name it was
        // seeded with.
        await _rooms.SyncPersonalRoomNameAsync(UserId);

        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }
        s.NativeLanguage = native;
        s.UiLanguage = ui;
        s.TranscriptionLanguage = transcription;
        s.JobTitle = Blank(req.JobTitle);
        s.CompanyName = Blank(req.CompanyName);
        s.JobDescription = Blank(req.JobDescription);
        s.CompanyDescription = Blank(req.CompanyDescription);
        s.LinkedIn = Blank(req.LinkedIn);
        s.Theme = ThemeFromString(req.Theme);
        await _db.SaveChangesAsync();

        // Re-issue the token so the updated name claim flows to the client without a re-login. (The new
        // profile fields + theme aren't claims, so they need no token change.)
        var (token, expires) = _tokens.CreateAccessToken(user);
        return new AuthResponse(token, expires);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string ThemeToString(ThemePreference t) => t switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "auto",
    };

    private static ThemePreference ThemeFromString(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => ThemePreference.Auto,
    };
}
