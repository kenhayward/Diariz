using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITokenService _tokens;
    private readonly IPlatformSettingsService _platform;

    public AuthController(UserManager<ApplicationUser> users, ITokenService tokens, IPlatformSettingsService platform)
    {
        _users = users;
        _tokens = tokens;
        _platform = platform;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req)
    {
        var user = await _users.FindByEmailAsync(req.Email);
        if (user is null) return Unauthorized();

        // Requested/Invited users have no password yet — give them the helpful next step rather than
        // a generic failure. (Acceptable status disclosure for an admin-gated internal tool.)
        if (user.Status == UserStatus.Requested)
            return StatusCode(StatusCodes.Status403Forbidden, "Your access request is awaiting approval.");
        if (user.Status == UserStatus.Invited)
            return StatusCode(StatusCodes.Status403Forbidden,
                "Please finish setting up your account using the link we sent you.");

        if (!await _users.CheckPasswordAsync(user, req.Password)) return Unauthorized();

        if (!user.IsEnabled)
            return StatusCode(StatusCodes.Status403Forbidden,
                "Your account has been disabled. Contact an administrator.");

        return await TokenResponse(user);
    }

    /// <summary>Public: anyone can request access. Neutral response (no account enumeration).</summary>
    [HttpPost("request-access")]
    public async Task<IActionResult> RequestAccess(RequestAccessRequest req)
    {
        var email = req.Email?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(email) && await _users.FindByEmailAsync(email) is null)
        {
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim(),
                Status = UserStatus.Requested,
                IsEnabled = true,
                EmailConfirmed = false,
                QuotaBytes = (await _platform.GetAsync()).StarterQuotaBytes,
            };
            var result = await _users.CreateAsync(user); // no password until setup
            if (result.Succeeded)
                await _users.AddToRoleAsync(user, Roles.Standard);
            // On failure (e.g. malformed email) stay silent — never reveal validity.
        }
        return Ok(new { message = "If that email is eligible, an administrator will review your request." });
    }

    /// <summary>Public: check a setup link before showing the form (rejects expired/garbage links).</summary>
    [HttpGet("setup/validate")]
    public async Task<ActionResult<SetupValidateResponse>> ValidateSetup(
        [FromQuery] string email, [FromQuery] string token)
    {
        var user = await _users.FindByEmailAsync(email ?? "");
        if (user is null || user.Status != UserStatus.Invited)
            return new SetupValidateResponse(false, null);
        var ok = await _users.VerifyUserTokenAsync(
            user, TokenOptions.DefaultProvider, AccountSetup.TokenPurpose, token ?? "");
        return new SetupValidateResponse(ok, ok ? user.Email : null, ok ? user.FullName : null);
    }

    /// <summary>Public: finish account setup — set full name + password, activate, auto sign-in.</summary>
    [HttpPost("setup")]
    public async Task<IActionResult> Setup(SetupRequest req)
    {
        var user = await _users.FindByEmailAsync(req.Email ?? "");
        if (user is null || user.Status != UserStatus.Invited
            || !await _users.VerifyUserTokenAsync(user, TokenOptions.DefaultProvider, AccountSetup.TokenPurpose, req.Token ?? ""))
            return BadRequest("This setup link is invalid or has expired.");

        if (string.IsNullOrWhiteSpace(req.FullName))
            return BadRequest("Please enter your full name.");

        var pwd = await _users.AddPasswordAsync(user, req.Password);
        if (!pwd.Succeeded)
            return BadRequest(pwd.Errors.Select(e => e.Description));

        user.FullName = req.FullName.Trim();
        user.Status = UserStatus.Active;
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user); // also rotates the security stamp → setup token is now single-use

        return await TokenResponse(user);
    }

    private async Task<IActionResult> TokenResponse(ApplicationUser user)
    {
        var (token, expires) = _tokens.CreateAccessToken(user, await _users.GetRolesAsync(user));
        return Ok(new AuthResponse(token, expires));
    }
}
