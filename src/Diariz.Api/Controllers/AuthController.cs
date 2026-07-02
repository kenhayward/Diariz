using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITokenService _tokens;
    private readonly IPlatformSettingsService _platform;
    private readonly IGoogleAuthService _google;
    private readonly IGoogleSignInHandler _googleSignIn;
    private readonly GoogleAuthOptions _googleOpts;
    private readonly AppPublicOptions _appOpts;
    private readonly ITimeLimitedDataProtector _stateProtector;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<ApplicationUser> users, ITokenService tokens, IPlatformSettingsService platform,
        IGoogleAuthService google, IGoogleSignInHandler googleSignIn, IOptions<GoogleAuthOptions> googleOpts,
        IOptions<AppPublicOptions> appOpts, IDataProtectionProvider dataProtection, ILogger<AuthController> logger)
    {
        _users = users;
        _tokens = tokens;
        _platform = platform;
        _google = google;
        _googleSignIn = googleSignIn;
        _googleOpts = googleOpts.Value;
        _appOpts = appOpts.Value;
        _stateProtector = dataProtection.CreateProtector("Diariz.GoogleOAuthState").ToTimeLimitedDataProtector();
        _logger = logger;
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

    /// <summary>Re-issue an access token for the signed-in user (a sliding session). Requires a still-valid
    /// token, so the client can refresh silently before expiry — keeping long sessions (e.g. a recording in
    /// progress) alive without a separate long-lived refresh token.</summary>
    [Authorize]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null || !user.IsEnabled) return Unauthorized();
        return await TokenResponse(user);
    }

    private async Task<IActionResult> TokenResponse(ApplicationUser user)
    {
        var (token, expires) = _tokens.CreateAccessToken(user, await _users.GetRolesAsync(user));
        return Ok(new AuthResponse(token, expires));
    }

    // ---- Google sign-in (server-side authorization-code + PKCE flow) ----

    private const string StateCookie = "diariz_g_oauth";
    private record OAuthState(string State, string Verifier);

    /// <summary>Public: which external sign-in providers are enabled (so the login page shows the button).</summary>
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new { google = _google.Enabled });

    /// <summary>Public: begin Google sign-in. Stashes PKCE state in a short-lived signed cookie and
    /// redirects to Google's consent screen.</summary>
    [HttpGet("google/start")]
    public IActionResult GoogleStart()
    {
        if (!_google.Enabled) return NotFound();

        var verifier = OAuthPkce.NewCodeVerifier();
        var state = OAuthPkce.NewState();
        var protectedState = _stateProtector.Protect(
            JsonSerializer.Serialize(new OAuthState(state, verifier)), TimeSpan.FromMinutes(10));
        Response.Cookies.Append(StateCookie, protectedState, StateCookieOptions());

        return Redirect(_google.BuildAuthorizationUrl(CallbackUri(), state, OAuthPkce.Challenge(verifier)));
    }

    /// <summary>Public: Google redirects here with the authorization code. Verifies state, exchanges the
    /// code, resolves the account, and bounces to the SPA with a token (or an error) — never returns JSON.</summary>
    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        if (!_google.Enabled) return NotFound();

        // The state cookie is one-time: always clear it, whatever the outcome.
        var cookie = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie, StateCookieOptions());

        // Failures are logged at Warning (the callback otherwise redirects to a friendly page with no trace)
        // and carry a distinct ?googleError= code so the cause is visible from the URL too.
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Google sign-in: provider returned error '{Error}'.", error);
            return RedirectToLogin("failed");
        }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            _logger.LogWarning("Google sign-in callback missing code or state.");
            return RedirectToLogin("failed");
        }
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.LogWarning("Google sign-in: state cookie absent on callback — the browser didn't return it "
                + "(SameSite/proxy) or the Data Protection keyring changed since /start.");
            return RedirectToLogin("failed_state");
        }

        OAuthState? saved;
        try { saved = JsonSerializer.Deserialize<OAuthState>(_stateProtector.Unprotect(cookie)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google sign-in: could not unprotect the state cookie — Data Protection "
                + "keyring mismatch (KeysPath volume not persisted/shared?) or the cookie expired.");
            return RedirectToLogin("failed_state");
        }
        if (saved is null || !FixedTimeEquals(saved.State, state))
        {
            _logger.LogWarning("Google sign-in: state mismatch (possible CSRF, or a stale/duplicate callback).");
            return RedirectToLogin("failed_state");
        }

        GoogleUserInfo info;
        try { info = await _google.ExchangeCodeAsync(code, saved.Verifier, CallbackUri()); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google sign-in: token exchange / ID-token validation failed "
                + "(redirect_uri={RedirectUri}). Common causes: wrong client secret, an unregistered redirect "
                + "URI, or the server can't reach oauth2.googleapis.com.", CallbackUri());
            return RedirectToLogin("failed_exchange");
        }

        var result = await _googleSignIn.SignInAsync(info);
        if (result.Outcome == GoogleSignInOutcome.Rejected)
            _logger.LogWarning("Google sign-in rejected for {Email}: {Reason}", info.Email, result.Reason);

        return result.Outcome switch
        {
            GoogleSignInOutcome.SignedIn => await SignedInRedirectAsync(result.User!),
            GoogleSignInOutcome.AwaitingApproval => RedirectToLogin("pending"),
            GoogleSignInOutcome.Disabled => RedirectToLogin("disabled"),
            _ => RedirectToLogin("failed"),
        };
    }

    private const string AuthHandoffCookie = "diariz_auth";
    private const string HandoffCookiePath = "/api/auth/google";
    private const string SpaCallbackPath = "/auth/google/callback";

    /// <summary>Hand the freshly-minted JWT to the SPA without ever putting it in a URL. The token rides in a
    /// short-lived, <b>HttpOnly</b> cookie scoped to the Google auth path; the SPA then trades it for the
    /// token via <c>POST google/exchange</c> (a JSON body, like normal login). This survives hostile reverse
    /// proxies that strip URL fragments <i>and</i> force <c>HttpOnly</c> on cookies (both of which defeat any
    /// JS-visible handoff). The token never appears in a URL, access log, or Referer.</summary>
    private async Task<IActionResult> SignedInRedirectAsync(ApplicationUser user)
    {
        var (token, _) = _tokens.CreateAccessToken(user, await _users.GetRolesAsync(user));
        Response.Cookies.Append(AuthHandoffCookie, token, HandoffCookieOptions());
        return Redirect($"{WebBase()}{SpaCallbackPath}");
    }

    private CookieOptions HandoffCookieOptions() => new()
    {
        HttpOnly = true,               // read server-side only (via the exchange endpoint)
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,   // stored from the top-level redirect; sent on the same-origin exchange
        Path = HandoffCookiePath,
        MaxAge = TimeSpan.FromMinutes(2),
    };

    /// <summary>Public: the SPA lands on <c>/auth/google/callback</c> after a successful Google sign-in and
    /// calls this to swap the one-time handoff cookie for its access token (returned in the JSON body, then
    /// the cookie is expired). 401 when there is no handoff cookie.</summary>
    [HttpPost("google/exchange")]
    public IActionResult GoogleExchange()
    {
        var token = Request.Cookies[AuthHandoffCookie];
        Response.Cookies.Delete(AuthHandoffCookie, HandoffCookieOptions());
        if (string.IsNullOrEmpty(token)) return Unauthorized();
        return Ok(new { accessToken = token }); // the SPA reads the token's expiry from the JWT itself
    }

    private CookieOptions StateCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,           // dev is plain http://localhost; prod is https
        SameSite = SameSiteMode.Lax,        // Lax so the top-level GET redirect from Google carries it
        Path = "/api/auth/google",
        MaxAge = TimeSpan.FromMinutes(10),
    };

    private string WebBase() =>
        !string.IsNullOrWhiteSpace(_appOpts.PublicUrl) ? _appOpts.PublicUrl.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";

    private string CallbackUri() =>
        !string.IsNullOrWhiteSpace(_googleOpts.RedirectUri) ? _googleOpts.RedirectUri : $"{WebBase()}/api/auth/google/callback";

    private IActionResult RedirectToLogin(string reason) => Redirect($"{WebBase()}/login?googleError={reason}");

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
