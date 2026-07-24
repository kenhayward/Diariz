using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
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
    private readonly DiarizDbContext _db;
    private readonly IGoogleTokenProtector _tokenProtector;
    private readonly IDesktopAuthCodeStore _desktopCodes;

    public AuthController(
        UserManager<ApplicationUser> users, ITokenService tokens, IPlatformSettingsService platform,
        IGoogleAuthService google, IGoogleSignInHandler googleSignIn, IOptions<GoogleAuthOptions> googleOpts,
        IOptions<AppPublicOptions> appOpts, IDataProtectionProvider dataProtection, ILogger<AuthController> logger,
        DiarizDbContext db, IGoogleTokenProtector tokenProtector, IDesktopAuthCodeStore desktopCodes)
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
        _db = db;
        _tokenProtector = tokenProtector;
        _desktopCodes = desktopCodes;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("login")]
    [EndpointSummary("Sign in with email and password")]
    [EndpointDescription(
        "Exchanges credentials for a **session token** and its expiry. Send it as `Authorization: Bearer ...` " +
        "on every other endpoint. There is no refresh token: call the refresh endpoint before this one " +
        "expires to extend the session.\n\n" +
        "For scripts and integrations, prefer a personal API token (`dz_api_...`) from Settings - it is " +
        "longer-lived, scope-limited, and revocable without changing your password.\n\n" +
        "401 for a wrong email or password. 403, with an explanation, when the account exists but cannot sign " +
        "in yet: awaiting approval, not yet set up, or disabled.")]
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
    [EndpointSummary("Request an account")]
    [EndpointDescription(
        "Registers interest in an account for an administrator to approve. **No authentication required.**\n\n" +
        "The response is deliberately the same whether or not the address already has an account, so this " +
        "cannot be used to discover who is registered. Nothing is granted here - approval creates the account " +
        "and sends a setup link.")]
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
    [EndpointSummary("Check an account setup link")]
    [EndpointDescription(
        "Tells you whether a setup link is still good before showing the form, so an expired or mistyped one " +
        "fails politely instead of after the user has filled everything in. **No authentication required.**\n\n" +
        "Always 200: a bad link comes back as `valid: false` rather than an error status, and reveals nothing " +
        "about the address. A valid one returns the email and name to pre-fill. This only checks - it does not " +
        "consume the link.")]
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
    [EndpointSummary("Finish setting up an account")]
    [EndpointDescription(
        "Completes an invited account: sets the full name and first password, activates it, and returns a " +
        "session token so the user is signed in straight away - no separate login call. **No authentication " +
        "required**, since the setup link is the credential.\n\n" +
        "The link is **single-use**: completing setup invalidates it. 400 for a link that is invalid, expired, " +
        "or already used, for a missing name, and for a password that fails the platform's rules (the response " +
        "lists what was wrong).")]
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
    [EndpointSummary("Extend the session")]
    [EndpointDescription(
        "Issues a fresh token for the signed-in user - a sliding session, so a long-running client (say, a " +
        "recording in progress) is never signed out mid-task.\n\n" +
        "Requires a **still-valid** token: there is no long-lived refresh token, so call this before the " +
        "current one expires. Once it has expired the user must sign in again. 401 for an expired token or a " +
        "since-disabled account.")]
    public async Task<IActionResult> Refresh()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null || !user.IsEnabled) return Unauthorized();
        return await TokenResponse(user);
    }

    private async Task<IActionResult> TokenResponse(ApplicationUser user)
    {
        var (token, expires) = _tokens.CreateAccessToken(user);
        return Ok(new AuthResponse(token, expires));
    }

    // ---- Google sign-in (server-side authorization-code + PKCE flow) ----

    private const string StateCookie = "diariz_g_oauth";
    /// <summary>State stashed in the signed cookie during /start or /connect. <c>Mode</c> = "signin" | "connect";
    /// <c>UserId</c> identifies the connecting user (set server-side during the authorized /connect);
    /// <c>DesktopChallenge</c> (non-null) marks a desktop sign-in and carries the app's S256 PKCE challenge.</summary>
    private record OAuthState(string State, string Verifier, string Mode = "signin", string? UserId = null,
        string? DesktopChallenge = null);

    /// <summary>Public: which external sign-in providers are enabled (so the login page shows the button).</summary>
    [HttpGet("providers")]
    [EndpointSummary("List the external sign-in providers")]
    [EndpointDescription(
        "Which external sign-in options this platform has configured, so a login page knows whether to show " +
        "the button. **No authentication required.** Currently just `google`. The rest of the Google flow " +
        "returns 404 when it is off.")]
    public IActionResult Providers() => Ok(new { google = _google.Enabled });

    /// <summary>Public: begin Google sign-in. Stashes PKCE state in a short-lived signed cookie and
    /// redirects to Google's consent screen. <paramref name="desktopChallenge"/> (from the desktop shell)
    /// marks this as a desktop flow so the callback hands back a diariz:// code instead of the SPA cookie.</summary>
    [HttpGet("google/start")]
    [EndpointSummary("Begin Google sign-in")]
    [EndpointDescription(
        "Starts the Google sign-in flow. **Navigate a browser here** - it does not return JSON, it redirects " +
        "to Google's consent screen after stashing PKCE state in a short-lived signed cookie. Not usable from " +
        "a script or a fetch call.\n\n" +
        "Google then returns to the callback endpoint. `desktopChallenge` marks a desktop sign-in, which hands " +
        "back a `diariz://` code instead of a browser cookie. 404 when Google sign-in is not configured.")]
    public IActionResult GoogleStart([FromQuery] string? desktopChallenge = null)
    {
        if (!_google.Enabled) return NotFound();

        var verifier = OAuthPkce.NewCodeVerifier();
        var state = OAuthPkce.NewState();
        var protectedState = _stateProtector.Protect(
            JsonSerializer.Serialize(new OAuthState(state, verifier, DesktopChallenge: desktopChallenge)),
            TimeSpan.FromMinutes(10));
        Response.Cookies.Append(StateCookie, protectedState, StateCookieOptions());

        return Redirect(_google.BuildAuthorizationUrl(
            CallbackUri(), state, OAuthPkce.Challenge(verifier), GoogleAuthService.SignInScope, offline: false));
    }

    /// <summary>Public: Google redirects here with the authorization code. Verifies state, exchanges the
    /// code, resolves the account, and bounces to the SPA with a token (or an error) — never returns JSON.</summary>
    [HttpGet("google/callback")]
    [EndpointSummary("Google sign-in callback")]
    [EndpointDescription(
        "Where Google returns after consent. **Not called by your code** - Google redirects the browser here. " +
        "It verifies the state, exchanges the authorization code, resolves the account, and redirects onward; " +
        "it never returns JSON.\n\n" +
        "The token is **never put in a URL**. A browser sign-in leaves it in a short-lived HttpOnly cookie " +
        "that the app trades in at the exchange endpoint; a desktop sign-in redirects to a `diariz://` link " +
        "carrying a single-use code. That way the token stays out of access logs, referrers, and browser " +
        "history.")]
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
            _logger.LogWarning("Google sign-in: provider returned error '{Error}'.", LogSanitizer.Clean(error));
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

        GoogleTokens tokens;
        try { tokens = await _google.ExchangeCodeAsync(code, saved.Verifier, CallbackUri()); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google {Mode}: token exchange failed (redirect_uri={RedirectUri}). Common "
                + "causes: wrong client secret, an unregistered redirect URI, or the server can't reach "
                + "oauth2.googleapis.com.", LogSanitizer.Clean(saved.Mode), CallbackUri());
            return saved.Mode == "connect" ? RedirectToApp("failed") : RedirectToLogin("failed_exchange");
        }

        // Data-access consent (Calendar): store the refresh token for the connecting user.
        if (saved.Mode == "connect")
            return await CompleteConnectAsync(saved, tokens);

        // Sign-in: validate the ID token → identity, then link/gate.
        GoogleUserInfo info;
        try { info = await _google.ValidateIdTokenAsync(tokens.IdToken ?? ""); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google sign-in: ID-token validation failed.");
            return RedirectToLogin("failed_exchange");
        }

        var result = await _googleSignIn.SignInAsync(info);
        if (result.Outcome == GoogleSignInOutcome.Rejected)
            _logger.LogWarning("Google sign-in rejected for {Email}: {Reason}",
                LogSanitizer.Clean(info.Email), LogSanitizer.Clean(result.Reason));

        return result.Outcome switch
        {
            GoogleSignInOutcome.SignedIn => saved.DesktopChallenge is { } challenge
                ? await DesktopSignedInRedirectAsync(result.User!, challenge)
                : await SignedInRedirectAsync(result.User!),
            GoogleSignInOutcome.AwaitingApproval => RedirectToLogin("pending"),
            GoogleSignInOutcome.Disabled => RedirectToLogin("disabled"),
            _ => RedirectToLogin("failed"),
        };
    }

    /// <summary>Store the granted data scopes + refresh token for the connecting user (from the signed state
    /// cookie), then bounce back to the app. Verifies the consenting Google account matches the linked one.</summary>
    private async Task<IActionResult> CompleteConnectAsync(OAuthState saved, GoogleTokens tokens)
    {
        if (!Guid.TryParse(saved.UserId, out var userId)) return RedirectToApp("failed");
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null) return RedirectToApp("failed");

        if (!string.IsNullOrEmpty(tokens.IdToken))
        {
            try
            {
                var info = await _google.ValidateIdTokenAsync(tokens.IdToken);
                if (user.GoogleSubject is not null && info.Subject != user.GoogleSubject)
                {
                    _logger.LogWarning("Google connect: consented account differs from the linked one.");
                    return RedirectToApp("mismatch");
                }
            }
            catch { /* if we can't validate identity, still store — the token is scoped to whoever consented */ }
        }

        var s = await _db.UserSettings.FindAsync(userId);
        if (s is null) { s = new UserSettings { UserId = userId }; _db.UserSettings.Add(s); }
        // Google only returns a refresh token on a fresh consent (we force prompt=consent); keep the old one otherwise.
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            s.GoogleRefreshTokenEncrypted = _tokenProtector.Protect(tokens.RefreshToken);
        var scope = tokens.Scope ?? "";
        s.GoogleCalendarGranted = scope.Contains(GoogleAuthService.CalendarReadScope);
        await _db.SaveChangesAsync();

        return RedirectToApp("connected", success: true);
    }

    /// <summary>Public: begin the incremental data-access consent for the signed-in user. Requires a linked
    /// Google account. Returns the Google consent URL for the SPA to navigate to.</summary>
    [Authorize]
    [HttpPost("google/connect")]
    [EndpointSummary("Connect Google Calendar")]
    [EndpointDescription(
        "Begins the extra consent needed to read your Google Calendar - separate from signing in, so nobody " +
        "grants calendar access just by logging in. Returns an `authorizationUrl` for the browser to navigate " +
        "to; consent happens at Google, then it redirects back.\n\n" +
        "Requires an already-linked Google account (400 otherwise) and at least one capability requested " +
        "(400 if none). 404 when Google sign-in is not configured on the platform.")]
    public async Task<IActionResult> GoogleConnect(ConnectGoogleRequest req)
    {
        if (!_google.Enabled) return NotFound();
        var user = await _users.FindByIdAsync(CurrentUserId.ToString());
        if (user is null) return Unauthorized();
        if (user.GoogleSubject is null)
            return BadRequest("Sign in with Google first to connect Calendar.");

        var scopes = new List<string> { "openid", "email" };
        if (req.Calendar) scopes.Add(GoogleAuthService.CalendarReadScope);
        if (scopes.Count == 2) return BadRequest("Choose at least one capability to connect.");

        var verifier = OAuthPkce.NewCodeVerifier();
        var state = OAuthPkce.NewState();
        var protectedState = _stateProtector.Protect(
            JsonSerializer.Serialize(new OAuthState(state, verifier, "connect", CurrentUserId.ToString())),
            TimeSpan.FromMinutes(10));
        Response.Cookies.Append(StateCookie, protectedState, StateCookieOptions());

        var url = _google.BuildAuthorizationUrl(
            CallbackUri(), state, OAuthPkce.Challenge(verifier), string.Join(' ', scopes), offline: true);
        return Ok(new { authorizationUrl = url });
    }

    /// <summary>Revoke Google data access for the signed-in user (revokes at Google, clears the stored token).</summary>
    [Authorize]
    [HttpPost("google/disconnect")]
    [EndpointSummary("Disconnect Google Calendar")]
    [EndpointDescription(
        "Revokes calendar access: the grant is revoked **at Google** as well as cleared here, so it does not " +
        "linger in your Google account permissions. Calendar events stop appearing.\n\n" +
        "This does not unlink Google **sign-in** - you can still log in with Google afterwards. Links already " +
        "made between recordings and calendar events are kept, since they are stored snapshots.")]
    public async Task<IActionResult> GoogleDisconnect(CancellationToken ct)
    {
        var s = await _db.UserSettings.FindAsync([CurrentUserId], ct);
        if (s?.GoogleRefreshTokenEncrypted is not null)
        {
            var refresh = _tokenProtector.Unprotect(s.GoogleRefreshTokenEncrypted);
            if (refresh is not null) await _google.RevokeAsync(refresh, ct);
            s.GoogleRefreshTokenEncrypted = null;
            s.GoogleCalendarGranted = false;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    /// <summary>Redirect back to the SPA root after a connect flow, with <c>?google=</c>/<c>?googleError=</c>.</summary>
    private IActionResult RedirectToApp(string reason, bool success = false) =>
        Redirect(SafeRedirect.Within($"{WebBase()}/?{(success ? "google" : "googleError")}={reason}", AllowedRedirectHosts()));

    private const string AuthHandoffCookie = "diariz_auth";
    private const string HandoffCookiePath = "/api/auth/google";
    private const string SpaCallbackPath = "/auth/google/callback";
    private const string DesktopCallbackUri = "diariz://auth/callback";

    /// <summary>Hand the freshly-minted JWT to the SPA without ever putting it in a URL. The token rides in a
    /// short-lived, <b>HttpOnly</b> cookie scoped to the Google auth path; the SPA then trades it for the
    /// token via <c>POST google/exchange</c> (a JSON body, like normal login). This survives hostile reverse
    /// proxies that strip URL fragments <i>and</i> force <c>HttpOnly</c> on cookies (both of which defeat any
    /// JS-visible handoff). The token never appears in a URL, access log, or Referer.</summary>
    private async Task<IActionResult> SignedInRedirectAsync(ApplicationUser user)
    {
        var (token, _) = _tokens.CreateAccessToken(user);
        Response.Cookies.Append(AuthHandoffCookie, token, HandoffCookieOptions());
        return Redirect(SafeRedirect.Within($"{WebBase()}{SpaCallbackPath}", AllowedRedirectHosts()));
    }

    /// <summary>Desktop sign-in handoff: mint a single-use code bound to the app's PKCE challenge and
    /// redirect the system browser to the diariz:// deep link. The JWT never rides in the URL - the app
    /// redeems the code at <c>POST desktop/exchange</c> by proving it holds the verifier. Deliberately a
    /// raw <see cref="ControllerBase.Redirect(string)"/> (not <see cref="SafeRedirect"/>): the custom
    /// scheme is fixed and only reachable from an encrypted-state desktop flow.</summary>
    private async Task<IActionResult> DesktopSignedInRedirectAsync(ApplicationUser user, string challenge)
    {
        var code = await _desktopCodes.MintAsync(user.Id, challenge, TimeSpan.FromMinutes(2));
        return Redirect($"{DesktopCallbackUri}?code={Uri.EscapeDataString(code)}");
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
    [EndpointSummary("Collect the token after Google sign-in")]
    [EndpointDescription(
        "The last step of a browser Google sign-in: swaps the one-time handoff cookie left by the callback " +
        "for your session token, returned in the **JSON body**. The cookie is expired as it is read, so this " +
        "works exactly once.\n\n" +
        "Handing the token over in a body rather than a URL fragment is deliberate: it survives reverse " +
        "proxies that strip fragments or force cookies to HttpOnly, either of which would break a " +
        "JavaScript-visible handoff. 401 when there is no handoff cookie. The response carries no expiry - " +
        "read it from the token itself.")]
    public IActionResult GoogleExchange()
    {
        var token = Request.Cookies[AuthHandoffCookie];
        Response.Cookies.Delete(AuthHandoffCookie, HandoffCookieOptions());
        if (string.IsNullOrEmpty(token)) return Unauthorized();
        return Ok(new { accessToken = token }); // the SPA reads the token's expiry from the JWT itself
    }

    /// <summary>Public: the desktop app swaps its one-time diariz:// code for an access token, proving it
    /// holds the PKCE verifier whose S256 challenge was bound to the code. Any failure -> generic 401.</summary>
    [HttpPost("desktop/exchange")]
    [EndpointSummary("Collect the token after desktop sign-in")]
    [EndpointDescription(
        "The desktop equivalent of the browser exchange: the app swaps the single-use code it received on its " +
        "`diariz://` deep link for a session token, proving it holds the PKCE verifier whose challenge was " +
        "bound to that code - so a code intercepted from the deep link is useless on its own.\n\n" +
        "The code is single-use and short-lived. **Any** failure - unknown code, already redeemed, expired, " +
        "wrong verifier - returns the same generic 401, so nothing can be probed.")]
    public async Task<IActionResult> DesktopExchange(DesktopExchangeRequest req)
    {
        // Diagnostic logging: the desktop app calls this from its main-process fetch after a diariz:// deep
        // link, and swallows any failure silently (the user just stays on the login screen). Logging the
        // arrival + the precise outcome makes the otherwise-invisible flow observable. If "request received"
        // never appears while the browser reached Google, the app's fetch isn't reaching the server (TLS/proxy
        // /network), not a code/challenge problem. No secrets are logged - only field lengths and the reason.
        _logger.LogInformation("Desktop sign-in exchange: request received (code {CodeLen} chars, verifier {VerifierLen} chars).",
            req.Code?.Length ?? 0, req.Verifier?.Length ?? 0);

        if (string.IsNullOrEmpty(req.Code) || string.IsNullOrEmpty(req.Verifier))
        {
            _logger.LogWarning("Desktop sign-in exchange: missing code or verifier -> 401.");
            return Unauthorized();
        }

        var ticket = await _desktopCodes.RedeemAsync(req.Code);
        if (ticket is null)
        {
            _logger.LogWarning("Desktop sign-in exchange: code not found / expired / already redeemed -> 401.");
            return Unauthorized();
        }
        if (!FixedTimeEquals(ticket.Challenge, OAuthPkce.Challenge(req.Verifier)))
        {
            _logger.LogWarning("Desktop sign-in exchange: PKCE challenge mismatch -> 401 (the verifier does not "
                + "match the challenge the code was minted with).");
            return Unauthorized();
        }

        var user = await _users.FindByIdAsync(ticket.UserId.ToString());
        if (user is null || !user.IsEnabled)
        {
            _logger.LogWarning("Desktop sign-in exchange: user missing or disabled -> 401.");
            return Unauthorized();
        }

        var (token, _) = _tokens.CreateAccessToken(user);
        _logger.LogInformation("Desktop sign-in exchange: success for {Email} -> access token issued (200).",
            LogSanitizer.Clean(user.Email ?? ""));
        return Ok(new { accessToken = token });
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

    /// <summary>Hosts a post-auth redirect may legitimately target: the configured public origin (preferred),
    /// else the trusted request host in dev. Passed to <see cref="SafeRedirect"/> so a spoofed <c>Host</c>
    /// header can't turn our redirects into an open redirect.</summary>
    private IReadOnlyCollection<string> AllowedRedirectHosts()
    {
        if (!string.IsNullOrWhiteSpace(_appOpts.PublicUrl) &&
            Uri.TryCreate(_appOpts.PublicUrl, UriKind.Absolute, out var pub))
            return [pub.Host];
        var host = ControllerContext?.HttpContext?.Request?.Host.Host;
        return string.IsNullOrEmpty(host) ? [] : [host];
    }

    private string CallbackUri() =>
        !string.IsNullOrWhiteSpace(_googleOpts.RedirectUri) ? _googleOpts.RedirectUri : $"{WebBase()}/api/auth/google/callback";

    private IActionResult RedirectToLogin(string reason) =>
        Redirect(SafeRedirect.Within($"{WebBase()}/login?googleError={reason}", AllowedRedirectHosts()));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
