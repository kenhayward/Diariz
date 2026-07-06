using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class AuthControllerTests
{
    private const string GoodPassword = "Sup3rSecret!";

    private static AuthController BuildController(
        IdentityTestHost host, IGoogleAuthService? google = null, AppPublicOptions? appOpts = null,
        GoogleAuthOptions? googleOpts = null, IDesktopAuthCodeStore? desktopCodes = null)
    {
        var tokens = new TokenService(Options.Create(new JwtOptions
        {
            Key = "test-signing-key-at-least-32-bytes-long!!",
            AccessTokenMinutes = 60,
        }));
        var platform = new PlatformSettingsService(host.Db);
        return new AuthController(
            host.Users, tokens, platform,
            google ?? new FakeGoogleAuthService { Enabled = false },
            new GoogleSignInHandler(host.Users, platform),
            Options.Create(googleOpts ?? new GoogleAuthOptions()),
            Options.Create(appOpts ?? new AppPublicOptions()),
            new EphemeralDataProtectionProvider(),
            NullLogger<AuthController>.Instance,
            host.Db,
            new GoogleTokenProtector(new EphemeralDataProtectionProvider()),
            desktopCodes ?? new FakeDesktopAuthCodeStore());
    }

    private static async Task<ApplicationUser> CreateUser(
        IdentityTestHost host, string email, string? password, UserStatus status, bool enabled = true)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, Status = status, IsEnabled = enabled,
            EmailConfirmed = status == UserStatus.Active,
        };
        if (password is null) await host.Users.CreateAsync(user);
        else await host.Users.CreateAsync(user, password);
        return user;
    }

    private static int? StatusOf(IActionResult r) => (r as ObjectResult)?.StatusCode;

    // ---- Login gating ----

    [Fact]
    public async Task Login_ActiveEnabled_ReturnsToken()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "a@x.test", GoodPassword, UserStatus.Active);

        var result = await BuildController(host).Login(new LoginRequest("a@x.test", GoodPassword));

        Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "a@x.test", GoodPassword, UserStatus.Active);

        Assert.IsType<UnauthorizedResult>(await BuildController(host).Login(new LoginRequest("a@x.test", "Wrong1!")));
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        Assert.IsType<UnauthorizedResult>(await BuildController(host).Login(new LoginRequest("ghost@x.test", GoodPassword)));
    }

    [Fact]
    public async Task Login_Requested_Returns403AwaitingApproval()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "req@x.test", null, UserStatus.Requested);

        var result = await BuildController(host).Login(new LoginRequest("req@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("awaiting approval", ((ObjectResult)result).Value!.ToString());
    }

    [Fact]
    public async Task Login_Invited_Returns403FinishSetup()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "inv@x.test", null, UserStatus.Invited);

        var result = await BuildController(host).Login(new LoginRequest("inv@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("finish setting up", ((ObjectResult)result).Value!.ToString());
    }

    [Fact]
    public async Task Login_Disabled_Returns403Disabled()
    {
        using var host = new IdentityTestHost();
        await CreateUser(host, "dis@x.test", GoodPassword, UserStatus.Active, enabled: false);

        var result = await BuildController(host).Login(new LoginRequest("dis@x.test", GoodPassword));

        Assert.Equal(403, StatusOf(result));
        Assert.Contains("disabled", ((ObjectResult)result).Value!.ToString());
    }

    // ---- Refresh (sliding session) ----

    [Fact]
    public async Task Refresh_AuthedEnabledUser_ReturnsNewToken()
    {
        using var host = new IdentityTestHost();
        var user = await CreateUser(host, "r@x.test", GoodPassword, UserStatus.Active);
        var controller = BuildController(host);
        controller.ControllerContext = Http.Context(user.Id);

        var result = await controller.Refresh();

        Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task Refresh_DisabledUser_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var user = await CreateUser(host, "d@x.test", GoodPassword, UserStatus.Active, enabled: false);
        var controller = BuildController(host);
        controller.ControllerContext = Http.Context(user.Id);

        Assert.IsType<UnauthorizedResult>(await controller.Refresh());
    }

    // ---- Request access ----

    [Fact]
    public async Task RequestAccess_CreatesRequestedStandardUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();

        await BuildController(host).RequestAccess(new RequestAccessRequest("new@x.test"));

        var user = await host.Users.FindByEmailAsync("new@x.test");
        Assert.NotNull(user);
        Assert.Equal(UserStatus.Requested, user!.Status);
        Assert.False(await host.Users.HasPasswordAsync(user)); // no password yet
        Assert.Contains(Roles.Standard, await host.Users.GetRolesAsync(user));
    }

    [Fact]
    public async Task RequestAccess_ExistingEmail_NeutralAndNoDuplicate()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        await CreateUser(host, "dupe@x.test", GoodPassword, UserStatus.Active);

        var result = await BuildController(host).RequestAccess(new RequestAccessRequest("dupe@x.test"));

        Assert.IsType<OkObjectResult>(result); // same neutral 200
        Assert.Equal(1, await host.Users.Users.CountAsync(u => u.Email == "dupe@x.test"));
    }

    // ---- Setup ----

    private static async Task<(ApplicationUser user, string token)> SeedInvited(IdentityTestHost host, string email)
    {
        await host.SeedRolesAsync();
        var user = await CreateUser(host, email, null, UserStatus.Invited);
        await host.Users.AddToRoleAsync(user, Roles.Standard);
        var token = await host.Users.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, AccountSetup.TokenPurpose);
        return (user, token);
    }

    [Fact]
    public async Task Setup_ValidToken_SetsPasswordNameActive_AndReturnsToken()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada Lovelace", GoodPassword));

        Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var user = await host.Users.FindByEmailAsync("set@x.test");
        Assert.Equal(UserStatus.Active, user!.Status);
        Assert.Equal("Ada Lovelace", user.FullName);
        Assert.True(user.EmailConfirmed);
        Assert.True(await host.Users.CheckPasswordAsync(user, GoodPassword));
    }

    [Fact]
    public async Task Setup_BadToken_ReturnsBadRequest_AndNoActivation()
    {
        using var host = new IdentityTestHost();
        await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", "not-a-token", "Ada", GoodPassword));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(UserStatus.Invited, (await host.Users.FindByEmailAsync("set@x.test"))!.Status);
    }

    [Fact]
    public async Task Setup_BlankFullName_ReturnsBadRequest()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "  ", GoodPassword));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Setup_WeakPassword_ReturnsIdentityErrors()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", "alllowercase"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsAssignableFrom<IEnumerable<string>>(bad.Value);
    }

    [Fact]
    public async Task Setup_Token_IsSingleUse()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");
        await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", GoodPassword));

        // Reusing the same link after activation must fail (status no longer Invited + stamp rotated).
        var again = await BuildController(host).Setup(new SetupRequest("set@x.test", token, "Ada", GoodPassword));
        Assert.IsType<BadRequestObjectResult>(again);
    }

    [Fact]
    public async Task ValidateSetup_ReflectsTokenValidity()
    {
        using var host = new IdentityTestHost();
        var (_, token) = await SeedInvited(host, "set@x.test");

        var good = (await BuildController(host).ValidateSetup("set@x.test", token)).Value!;
        var bad = (await BuildController(host).ValidateSetup("set@x.test", "garbage")).Value!;

        Assert.True(good.Valid);
        Assert.Equal("set@x.test", good.Email);
        Assert.False(bad.Valid);
    }

    // ---- Google sign-in ----

    private const string WebBase = "http://localhost:8081";
    private static AppPublicOptions PublicOpts => new() { PublicUrl = WebBase };

    [Fact]
    public void Providers_ReflectsWhetherGoogleIsEnabled()
    {
        using var host = new IdentityTestHost();

        var on = BuildController(host, new FakeGoogleAuthService { Enabled = true });
        var off = BuildController(host, new FakeGoogleAuthService { Enabled = false });

        Assert.Equal("{ google = True }", ((OkObjectResult)on.Providers()).Value!.ToString());
        Assert.Equal("{ google = False }", ((OkObjectResult)off.Providers()).Value!.ToString());
    }

    [Fact]
    public void GoogleStart_WhenDisabled_ReturnsNotFound()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = false });
        controller.ControllerContext = Http.Context();

        Assert.IsType<NotFoundResult>(controller.GoogleStart());
    }

    [Fact]
    public void GoogleStart_WhenEnabled_RedirectsToGoogle_AndSetsStateCookie()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts);
        controller.ControllerContext = Http.Context();

        var redirect = Assert.IsType<RedirectResult>(controller.GoogleStart());

        Assert.StartsWith("https://accounts.google.com/", redirect.Url);
        Assert.Contains("diariz_g_oauth=", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task GoogleCallback_WhenDisabled_ReturnsNotFound()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = false });
        controller.ControllerContext = Http.Context();

        Assert.IsType<NotFoundResult>(await controller.GoogleCallback("code", "state", null));
    }

    [Fact]
    public async Task GoogleCallback_MissingCode_RedirectsToLoginWithError()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts);
        controller.ControllerContext = Http.Context();

        var redirect = Assert.IsType<RedirectResult>(await controller.GoogleCallback(code: null, state: null, error: "access_denied"));

        Assert.Equal($"{WebBase}/login?googleError=failed", redirect.Url);
    }

    [Fact]
    public async Task GoogleCallback_HappyPath_LinkedActiveUser_RedirectsToSpaWithToken()
    {
        using var host = new IdentityTestHost();
        await CreateGoogleUser(host, "g@x.test", "google-sub", UserStatus.Active);
        var google = new FakeGoogleAuthService
        {
            Enabled = true,
            Result = new GoogleUserInfo("google-sub", "g@x.test", true, "Grace", "https://pic/g.png", null),
        };
        var controller = BuildController(host, google, PublicOpts);

        // Start sets the one-time state cookie; reuse the same controller so its state protector matches.
        controller.ControllerContext = Http.Context();
        controller.GoogleStart();
        var cookie = CookieValue(controller.Response.Headers.SetCookie.ToString(), "diariz_g_oauth");

        // Callback with the captured state + the cookie echoed back (as the browser would).
        var cbCtx = Http.Context();
        cbCtx.HttpContext.Request.Headers["Cookie"] = $"diariz_g_oauth={cookie}";
        controller.ControllerContext = cbCtx;
        var redirect = Assert.IsType<RedirectResult>(
            await controller.GoogleCallback("auth-code", google.CapturedState, null));

        // Token is delivered via a same-origin cookie (proxy-safe), not a URL fragment.
        Assert.Equal($"{WebBase}/auth/google/callback", redirect.Url);
        Assert.Contains("diariz_auth=", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task GoogleCallback_DesktopFlow_RedirectsToDeepLinkWithMintedCode()
    {
        using var host = new IdentityTestHost();
        await CreateGoogleUser(host, "g@x.test", "google-sub", UserStatus.Active);
        var google = new FakeGoogleAuthService
        {
            Enabled = true,
            Result = new GoogleUserInfo("google-sub", "g@x.test", true, "Grace", "https://pic/g.png", null),
        };
        var codes = new FakeDesktopAuthCodeStore();
        var controller = BuildController(host, google, PublicOpts, desktopCodes: codes);

        // Start WITH a desktop challenge sets a desktop-marked state cookie.
        controller.ControllerContext = Http.Context();
        controller.GoogleStart(desktopChallenge: "CHAL");
        var cookie = CookieValue(controller.Response.Headers.SetCookie.ToString(), "diariz_g_oauth");

        var cbCtx = Http.Context();
        cbCtx.HttpContext.Request.Headers["Cookie"] = $"diariz_g_oauth={cookie}";
        controller.ControllerContext = cbCtx;
        var redirect = Assert.IsType<RedirectResult>(
            await controller.GoogleCallback("auth-code", google.CapturedState, null));

        // Hands back via the custom scheme, carrying a one-time code (not a token).
        Assert.StartsWith("diariz://auth/callback?code=", redirect.Url);
        // No SPA handoff cookie on the desktop path.
        Assert.DoesNotContain("diariz_auth=", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task GoogleCallback_PendingUser_RedirectsToLoginPending()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var google = new FakeGoogleAuthService
        {
            Enabled = true,
            Result = new GoogleUserInfo("new-sub", "new@x.test", true, "New", null, null),
        };
        var controller = BuildController(host, google, PublicOpts);

        controller.ControllerContext = Http.Context();
        controller.GoogleStart();
        var cookie = CookieValue(controller.Response.Headers.SetCookie.ToString(), "diariz_g_oauth");
        var cbCtx = Http.Context();
        cbCtx.HttpContext.Request.Headers["Cookie"] = $"diariz_g_oauth={cookie}";
        controller.ControllerContext = cbCtx;

        var redirect = Assert.IsType<RedirectResult>(
            await controller.GoogleCallback("auth-code", google.CapturedState, null));

        Assert.Equal($"{WebBase}/login?googleError=pending", redirect.Url);
        Assert.Equal(UserStatus.Requested, (await host.Users.FindByEmailAsync("new@x.test"))!.Status);
    }

    [Fact]
    public async Task GoogleCallback_StateMismatch_RedirectsToLoginFailed()
    {
        using var host = new IdentityTestHost();
        var google = new FakeGoogleAuthService { Enabled = true, Result = new GoogleUserInfo("s", "e@x.test", true, null, null, null) };
        var controller = BuildController(host, google, PublicOpts);

        controller.ControllerContext = Http.Context();
        controller.GoogleStart();
        var cookie = CookieValue(controller.Response.Headers.SetCookie.ToString(), "diariz_g_oauth");
        var cbCtx = Http.Context();
        cbCtx.HttpContext.Request.Headers["Cookie"] = $"diariz_g_oauth={cookie}";
        controller.ControllerContext = cbCtx;

        var redirect = Assert.IsType<RedirectResult>(
            await controller.GoogleCallback("auth-code", "not-the-saved-state", null));

        Assert.Equal($"{WebBase}/login?googleError=failed_state", redirect.Url);
    }

    [Fact]
    public void GoogleExchange_WithHandoffCookie_ReturnsTokenAndClearsCookie()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true });
        var ctx = Http.Context();
        ctx.HttpContext.Request.Headers["Cookie"] = "diariz_auth=my.jwt.token";
        controller.ControllerContext = ctx;

        var ok = Assert.IsType<OkObjectResult>(controller.GoogleExchange());

        Assert.Equal("{ accessToken = my.jwt.token }", ok.Value!.ToString());
        // The one-time handoff cookie is expired on the way out.
        Assert.Contains("diariz_auth=", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void GoogleExchange_WithoutCookie_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true });
        controller.ControllerContext = Http.Context();

        Assert.IsType<UnauthorizedResult>(controller.GoogleExchange());
    }

    // ---- Google data connect / disconnect (Phase 2) ----

    [Fact]
    public async Task GoogleConnect_RequiresALinkedGoogleAccount()
    {
        using var host = new IdentityTestHost();
        var user = await CreateUser(host, "p@x.test", GoodPassword, UserStatus.Active); // password-only, no GoogleSubject
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts);
        controller.ControllerContext = Http.Context(user.Id);

        Assert.IsType<BadRequestObjectResult>(await controller.GoogleConnect(new ConnectGoogleRequest(Calendar: true)));
    }

    [Fact]
    public async Task GoogleConnect_BuildsOfflineConsentUrl_ForTheTickedScopes()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "sub-1", UserStatus.Active);
        var google = new FakeGoogleAuthService { Enabled = true };
        var controller = BuildController(host, google, PublicOpts);
        controller.ControllerContext = Http.Context(user.Id);

        var ok = Assert.IsType<OkObjectResult>(await controller.GoogleConnect(new ConnectGoogleRequest(Calendar: true)));

        Assert.Contains("authorizationUrl", ok.Value!.ToString());
        Assert.True(google.CapturedOffline);
        Assert.Contains("calendar.readonly", google.CapturedScope);
        Assert.Contains("diariz_g_oauth=", controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task GoogleConnect_NoScopesTicked_BadRequest()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "sub-1", UserStatus.Active);
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts);
        controller.ControllerContext = Http.Context(user.Id);

        Assert.IsType<BadRequestObjectResult>(await controller.GoogleConnect(new ConnectGoogleRequest(false)));
    }

    [Fact]
    public async Task GoogleCallback_ConnectMode_StoresGrantedScopesAndRefreshToken()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "sub-1", UserStatus.Active);
        var google = new FakeGoogleAuthService
        {
            Enabled = true,
            Result = new GoogleUserInfo("sub-1", "g@x.test", true, "G", null, null), // matches the linked account
            Tokens = new GoogleTokens("access", "refresh-new", 3600, $"openid email {GoogleAuthService.CalendarReadScope}", "id-token"),
        };
        var controller = BuildController(host, google, PublicOpts);

        // Start the connect (sets the signed state cookie on this controller instance).
        controller.ControllerContext = Http.Context(user.Id);
        await controller.GoogleConnect(new ConnectGoogleRequest(Calendar: true));
        var cookie = CookieValue(controller.Response.Headers.SetCookie.ToString(), "diariz_g_oauth");

        // Google redirects back to the callback with the captured state + the cookie echoed.
        var cbCtx = Http.Context();
        cbCtx.HttpContext.Request.Headers["Cookie"] = $"diariz_g_oauth={cookie}";
        controller.ControllerContext = cbCtx;
        var redirect = Assert.IsType<RedirectResult>(await controller.GoogleCallback("auth-code", google.CapturedState, null));

        Assert.Equal($"{WebBase}/?google=connected", redirect.Url);
        var s = await host.Db.UserSettings.FindAsync(user.Id);
        Assert.NotNull(s!.GoogleRefreshTokenEncrypted);
        Assert.True(s.GoogleCalendarGranted);
    }

    [Fact]
    public async Task GoogleDisconnect_ClearsStoredTokenAndGrantFlags()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "sub-1", UserStatus.Active);
        host.Db.UserSettings.Add(new UserSettings
        {
            UserId = user.Id, GoogleRefreshTokenEncrypted = "enc", GoogleCalendarGranted = true,
        });
        await host.Db.SaveChangesAsync();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true });
        controller.ControllerContext = Http.Context(user.Id);

        Assert.IsType<NoContentResult>(await controller.GoogleDisconnect(default));

        var s = await host.Db.UserSettings.FindAsync(user.Id);
        Assert.Null(s!.GoogleRefreshTokenEncrypted);
        Assert.False(s.GoogleCalendarGranted);
    }

    // ---- Desktop code exchange ----

    [Fact]
    public async Task DesktopExchange_ValidCodeAndVerifier_ReturnsAccessToken()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "google-sub", UserStatus.Active);
        var codes = new FakeDesktopAuthCodeStore();
        const string verifier = "test-verifier-value";
        var code = await codes.MintAsync(user.Id, OAuthPkce.Challenge(verifier), TimeSpan.FromMinutes(2));
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts, desktopCodes: codes);
        controller.ControllerContext = Http.Context();

        var ok = Assert.IsType<OkObjectResult>(await controller.DesktopExchange(new DesktopExchangeRequest(code, verifier)));
        Assert.NotNull(ok.Value!.GetType().GetProperty("accessToken")!.GetValue(ok.Value));
    }

    [Fact]
    public async Task DesktopExchange_WrongVerifier_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "google-sub", UserStatus.Active);
        var codes = new FakeDesktopAuthCodeStore();
        var code = await codes.MintAsync(user.Id, OAuthPkce.Challenge("right-verifier"), TimeSpan.FromMinutes(2));
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts, desktopCodes: codes);
        controller.ControllerContext = Http.Context();

        Assert.IsType<UnauthorizedResult>(await controller.DesktopExchange(new DesktopExchangeRequest(code, "wrong-verifier")));
    }

    [Fact]
    public async Task DesktopExchange_UnknownCode_ReturnsUnauthorized()
    {
        using var host = new IdentityTestHost();
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts, desktopCodes: new FakeDesktopAuthCodeStore());
        controller.ControllerContext = Http.Context();

        Assert.IsType<UnauthorizedResult>(await controller.DesktopExchange(new DesktopExchangeRequest("code-does-not-exist", "v")));
    }

    [Fact]
    public async Task DesktopExchange_CodeIsSingleUse_SecondCallUnauthorized()
    {
        using var host = new IdentityTestHost();
        var user = await CreateGoogleUser(host, "g@x.test", "google-sub", UserStatus.Active);
        var codes = new FakeDesktopAuthCodeStore();
        const string verifier = "test-verifier-value";
        var code = await codes.MintAsync(user.Id, OAuthPkce.Challenge(verifier), TimeSpan.FromMinutes(2));
        var controller = BuildController(host, new FakeGoogleAuthService { Enabled = true }, PublicOpts, desktopCodes: codes);
        controller.ControllerContext = Http.Context();

        Assert.IsType<OkObjectResult>(await controller.DesktopExchange(new DesktopExchangeRequest(code, verifier)));
        Assert.IsType<UnauthorizedResult>(await controller.DesktopExchange(new DesktopExchangeRequest(code, verifier)));
    }

    private static async Task<ApplicationUser> CreateGoogleUser(
        IdentityTestHost host, string email, string sub, UserStatus status)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, Status = status, IsEnabled = true,
            EmailConfirmed = true, GoogleSubject = sub,
        };
        await host.Users.CreateAsync(user);
        return user;
    }

    private static string CookieValue(string setCookieHeader, string name)
    {
        // "name=VALUE; path=/...; ..." → VALUE
        var start = setCookieHeader.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1;
        var end = setCookieHeader.IndexOf(';', start);
        return setCookieHeader[start..(end < 0 ? setCookieHeader.Length : end)];
    }

    private sealed class FakeGoogleAuthService : IGoogleAuthService
    {
        public bool Enabled { get; init; }
        public GoogleUserInfo? Result { get; init; }
        public GoogleTokens Tokens { get; init; } = new(null, "refresh-x", 3600, "openid email", "id-token");
        public string CapturedState { get; private set; } = "";
        public bool CapturedOffline { get; private set; }
        public string CapturedScope { get; private set; } = "";

        public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, string scope, bool offline)
        {
            CapturedState = state;
            CapturedOffline = offline;
            CapturedScope = scope;
            return $"https://accounts.google.com/o/oauth2/v2/auth?state={state}";
        }

        public Task<GoogleTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default) =>
            Task.FromResult(Tokens);
        public Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) => Task.FromResult(Tokens);
        public Task<GoogleUserInfo> ValidateIdTokenAsync(string idToken) => Task.FromResult(Result!);
        public Task RevokeAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }
}
