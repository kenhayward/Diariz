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

    public UserProfileController(UserManager<ApplicationUser> users, DiarizDbContext db, ITokenService tokens)
    {
        _users = users;
        _db = db;
        _tokens = tokens;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<UserProfileDto>> Get()
    {
        var user = await _users.FindByIdAsync(UserId.ToString());
        if (user is null) return NotFound();
        var s = await _db.UserSettings.FindAsync(UserId);
        return new UserProfileDto(user.Email ?? "", user.FullName, s?.NativeLanguage, s?.UiLanguage,
            GoogleConnected: user.GoogleSubject is not null,
            GoogleCalendar: s?.GoogleCalendarGranted ?? false,
            GoogleGmail: s?.GoogleGmailGranted ?? false);
    }

    [HttpPut]
    public async Task<ActionResult<AuthResponse>> Update(UpdateUserProfileRequest req)
    {
        var user = await _users.FindByIdAsync(UserId.ToString());
        if (user is null) return NotFound();

        var native = Blank(req.NativeLanguage);
        var ui = Blank(req.UiLanguage);
        if (native is not null && !SupportedLanguages.IsSupported(native))
            return BadRequest("Unknown native language.");
        if (ui is not null && !SupportedLanguages.IsSupported(ui))
            return BadRequest("Unknown UI language.");

        user.FullName = Blank(req.FullName);
        await _users.UpdateAsync(user);

        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }
        s.NativeLanguage = native;
        s.UiLanguage = ui;
        await _db.SaveChangesAsync();

        // Re-issue the token so the updated name claim flows to the client without a re-login.
        var (token, expires) = _tokens.CreateAccessToken(user, await _users.GetRolesAsync(user));
        return new AuthResponse(token, expires);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
