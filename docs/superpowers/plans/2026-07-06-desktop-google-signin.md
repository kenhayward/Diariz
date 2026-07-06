# Desktop Google Sign-in Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Google-authenticated users sign in from the Diariz desktop (Electron) client, which today only offers username/password - locking out Google-only users (who have no password).

**Architecture:** Reuse the existing server-side confidential Google OAuth flow unchanged. The desktop shell opens consent in the **system browser**; the server, seeing a desktop-marked flow, hands back a **single-use, short-TTL code** (Redis) via a **`diariz://` custom-protocol** deep link; the app exchanges that code (proving it holds a PKCE verifier) for the JWT and injects it into the SPA. No Google-console change; the JWT never travels in a URL.

**Tech Stack:** ASP.NET Core 10 + StackExchange.Redis (API), Electron + `node --test` (desktop), React 19 + Vitest (web).

**Reference spec:** `docs/superpowers/specs/2026-07-06-desktop-google-signin-design.md`

**Refinement vs spec:** the spec described `OAuthState` gaining a `Desktop` bool + `Challenge`. This plan collapses them into one nullable field, `DesktopChallenge` (null = not a desktop flow), which is equivalent and avoids an inconsistent `Desktop=true, Challenge=null` combination. The `/start` query param is `desktopChallenge` (its presence signals desktop intent).

**PR / version structure** (three PRs; each ships one release per the repo rule). Numbers below assume `main` is at **0.97.8** - rebase if it advanced:
- **PR 1 - API** (Redis code store + `/start` marker + desktop callback + `/desktop/exchange`): internal plumbing, not user-visible alone -> **Build +1 -> 0.97.9**. Server redeploy.
- **PR 2 - Desktop shell** (`diariz://` registration + deep-link handling + IPC): **Build +1 -> 0.97.10**. **Desktop release** (new installer registers the scheme).
- **PR 3 - Web** (Google button in Electron + AuthProvider token intake): surfaces the feature -> **Minor +1 -> 0.98.0**. Server redeploy.

Each PR: branch off `main`, commit per task, open PR, watch CI, wait for merge. Never `git add -A` (stage by path; the local-only `untracked/` and `docs/superpowers/` working files are not part of these PRs unless a task says so).

---

## PR 1 - API: one-time code store, desktop start marker, desktop callback + exchange

Branch: `feat/desktop-google-signin-api`

### Task 1: `IDesktopAuthCodeStore` + Redis implementation

**Files:**
- Create: `src/Diariz.Api/Services/DesktopAuthCodeStore.cs`
- Modify: `src/Diariz.Api/Program.cs` (register the service near the Redis block, ~line 147)
- Test (integration): `tests/Diariz.Api.IntegrationTests/DesktopAuthCodeStoreTests.cs`

- [ ] **Step 1: Write the failing integration test**

Create `tests/Diariz.Api.IntegrationTests/DesktopAuthCodeStoreTests.cs`:

```csharp
using Diariz.Api.Services;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class DesktopAuthCodeStoreTests(ContainersFixture fx)
{
    [Fact]
    public async Task Mint_then_redeem_returns_ticket_exactly_once()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var store = new RedisDesktopAuthCodeStore(redis);
        var uid = Guid.NewGuid();

        var code = await store.MintAsync(uid, "chal-abc", TimeSpan.FromMinutes(2));
        var first = await store.RedeemAsync(code);
        var second = await store.RedeemAsync(code);

        Assert.NotNull(first);
        Assert.Equal(uid, first!.UserId);
        Assert.Equal("chal-abc", first.Challenge);
        Assert.Null(second); // single-use: GETDEL removed it
    }

    [Fact]
    public async Task Redeem_unknown_code_returns_null()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var store = new RedisDesktopAuthCodeStore(redis);
        Assert.Null(await store.RedeemAsync("nope"));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~DesktopAuthCodeStore"`
Expected: FAIL to compile (`RedisDesktopAuthCodeStore` / `DesktopAuthTicket` don't exist). (Requires Docker.)

- [ ] **Step 3: Implement the store**

Create `src/Diariz.Api/Services/DesktopAuthCodeStore.cs`:

```csharp
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>A redeemed desktop sign-in code: who it was minted for, and the PKCE challenge the
/// initiating desktop app must prove it holds the verifier for.</summary>
public sealed record DesktopAuthTicket(Guid UserId, string Challenge);

/// <summary>Single-use, short-TTL codes that bridge the browser Google callback to the desktop app.
/// The code travels via the diariz:// deep link; redemption is one-shot (defence in depth alongside
/// the PKCE-style verifier check at exchange time).</summary>
public interface IDesktopAuthCodeStore
{
    Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl);
    Task<DesktopAuthTicket?> RedeemAsync(string code);
}

public sealed class RedisDesktopAuthCodeStore(IConnectionMultiplexer redis) : IDesktopAuthCodeStore
{
    private const string Prefix = "desktop-auth-code:";

    public async Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl)
    {
        var code = OAuthPkce.NewState(); // 43-char base64url, URL-safe for the deep link
        var payload = $"{userId:N}:{challenge}";
        await redis.GetDatabase().StringSetAsync(Prefix + code, payload, ttl);
        return code;
    }

    public async Task<DesktopAuthTicket?> RedeemAsync(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        // GETDEL: atomic read-and-delete, so a code redeems exactly once.
        var value = await redis.GetDatabase().StringGetDeleteAsync(Prefix + code);
        if (value.IsNullOrEmpty) return null;
        var parts = ((string)value!).Split(':', 2);
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var uid)
            ? new DesktopAuthTicket(uid, parts[1])
            : null;
    }
}
```

- [ ] **Step 4: Register the service**

In `src/Diariz.Api/Program.cs`, immediately after the `IJobQueue` registration (line ~147):

```csharp
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddSingleton<IDesktopAuthCodeStore, RedisDesktopAuthCodeStore>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~DesktopAuthCodeStore"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/DesktopAuthCodeStore.cs src/Diariz.Api/Program.cs tests/Diariz.Api.IntegrationTests/DesktopAuthCodeStoreTests.cs
git commit -m "feat(api): Redis-backed single-use desktop sign-in code store"
```

---

### Task 2: Add the in-memory fake + exchange DTO

**Files:**
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs` (append a fake)
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (add the request record)

- [ ] **Step 1: Add the fake**

Append to `tests/Diariz.Api.TestSupport/Fakes.cs` (inside the existing `namespace Diariz.Api.Tests.Infrastructure;`):

```csharp
/// <summary>In-memory <see cref="IDesktopAuthCodeStore"/> for unit tests: deterministic codes
/// (code-1, code-2, …) and one-shot redemption, mirroring the Redis GETDEL contract.</summary>
public sealed class FakeDesktopAuthCodeStore : Diariz.Api.Services.IDesktopAuthCodeStore
{
    private readonly Dictionary<string, Diariz.Api.Services.DesktopAuthTicket> _codes = new();
    private int _seq;

    public Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl)
    {
        var code = $"code-{++_seq}";
        _codes[code] = new Diariz.Api.Services.DesktopAuthTicket(userId, challenge);
        return Task.FromResult(code);
    }

    public Task<Diariz.Api.Services.DesktopAuthTicket?> RedeemAsync(string code)
    {
        if (code is not null && _codes.Remove(code, out var ticket))
            return Task.FromResult<Diariz.Api.Services.DesktopAuthTicket?>(ticket);
        return Task.FromResult<Diariz.Api.Services.DesktopAuthTicket?>(null);
    }
}
```

(If `Fakes.cs` already `using Diariz.Api.Services;`, drop the qualifiers; otherwise the fully-qualified names above keep it self-contained.)

- [ ] **Step 2: Add the exchange DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, add near the other auth request records (`LoginRequest`, `AuthResponse`):

```csharp
/// <summary>Desktop app -> API: swap a one-time diariz:// code for an access token, proving the app
/// holds the PKCE verifier whose S256 challenge was bound to the code.</summary>
public record DesktopExchangeRequest(string Code, string Verifier);
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Diariz.slnx`
Expected: build succeeds (the fake + DTO compile; nothing consumes them yet).

- [ ] **Step 4: Commit**

```bash
git add tests/Diariz.Api.TestSupport/Fakes.cs src/Diariz.Api/Contracts/ApiDtos.cs
git commit -m "test(api): FakeDesktopAuthCodeStore + DesktopExchangeRequest DTO"
```

---

### Task 3: Thread the code store into AuthController + carry the desktop marker in state

**Files:**
- Modify: `src/Diariz.Api/Controllers/AuthController.cs`
- Modify: `tests/Diariz.Api.Tests/AuthControllerTests.cs` (update `BuildController` to inject the fake)

This task is a non-behavioural wiring change (constructor param + `OAuthState` field + `/start` query param), so it has no new test of its own; Step 4 confirms the existing suite still passes.

- [ ] **Step 1: Add the field + constructor param**

In `src/Diariz.Api/Controllers/AuthController.cs`, add a field alongside the others (after `_tokenProtector`):

```csharp
    private readonly IGoogleTokenProtector _tokenProtector;
    private readonly IDesktopAuthCodeStore _desktopCodes;
```

Add the parameter to the constructor signature (after `IGoogleTokenProtector tokenProtector`):

```csharp
        DiarizDbContext db, IGoogleTokenProtector tokenProtector, IDesktopAuthCodeStore desktopCodes)
```

And assign it in the body (after `_tokenProtector = tokenProtector;`):

```csharp
        _tokenProtector = tokenProtector;
        _desktopCodes = desktopCodes;
```

- [ ] **Step 2: Add `DesktopChallenge` to `OAuthState`**

Replace the `OAuthState` record (the private record near `StateCookie`):

```csharp
    /// <summary>State stashed in the signed cookie during /start or /connect. <c>Mode</c> = "signin" | "connect";
    /// <c>UserId</c> identifies the connecting user (set server-side during the authorized /connect);
    /// <c>DesktopChallenge</c> (non-null) marks a desktop sign-in and carries the app's S256 PKCE challenge.</summary>
    private record OAuthState(string State, string Verifier, string Mode = "signin", string? UserId = null,
        string? DesktopChallenge = null);
```

- [ ] **Step 3: Accept the desktop marker at `/start`**

Replace the `GoogleStart` method:

```csharp
    /// <summary>Public: begin Google sign-in. Stashes PKCE state in a short-lived signed cookie and
    /// redirects to Google's consent screen. <paramref name="desktopChallenge"/> (from the desktop shell)
    /// marks this as a desktop flow so the callback hands back a diariz:// code instead of the SPA cookie.</summary>
    [HttpGet("google/start")]
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
```

- [ ] **Step 4: Update the unit-test controller factory, then run the suite**

In `tests/Diariz.Api.Tests/AuthControllerTests.cs`, change `BuildController` to create and inject a fake. Add a parameter and pass it to the constructor (last argument):

```csharp
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
```

Add `using Diariz.Api.Services;` to the test file if not already present (for `IDesktopAuthCodeStore`).

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AuthControllerTests"`
Expected: PASS (all existing AuthController tests still green - behaviour unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/AuthController.cs tests/Diariz.Api.Tests/AuthControllerTests.cs
git commit -m "feat(api): carry desktop PKCE challenge through Google /start state"
```

---

### Task 4: Desktop branch in the callback (emit the diariz:// deep link)

**Files:**
- Modify: `src/Diariz.Api/Controllers/AuthController.cs`
- Test: `tests/Diariz.Api.Tests/AuthControllerTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `AuthControllerTests.cs` (near the other Google callback tests). It reuses the exact start->callback cookie plumbing the happy-path test uses:

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name=GoogleCallback_DesktopFlow_RedirectsToDeepLinkWithMintedCode"`
Expected: FAIL - the callback currently always calls `SignedInRedirectAsync` (asserts `diariz://…` but gets the SPA URL).

- [ ] **Step 3: Implement the desktop branch**

In `GoogleCallback`, change the `SignedIn` arm of the final `switch` to branch on the desktop marker:

```csharp
        return result.Outcome switch
        {
            GoogleSignInOutcome.SignedIn => saved.DesktopChallenge is { } challenge
                ? await DesktopSignedInRedirectAsync(result.User!, challenge)
                : await SignedInRedirectAsync(result.User!),
            GoogleSignInOutcome.AwaitingApproval => RedirectToLogin("pending"),
            GoogleSignInOutcome.Disabled => RedirectToLogin("disabled"),
            _ => RedirectToLogin("failed"),
        };
```

Add the new helper next to `SignedInRedirectAsync`:

```csharp
    private const string DesktopCallbackUri = "diariz://auth/callback";

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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name=GoogleCallback_DesktopFlow_RedirectsToDeepLinkWithMintedCode"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/AuthController.cs tests/Diariz.Api.Tests/AuthControllerTests.cs
git commit -m "feat(api): desktop Google callback hands back a diariz:// one-time code"
```

---

### Task 5: `POST /api/auth/desktop/exchange`

**Files:**
- Modify: `src/Diariz.Api/Controllers/AuthController.cs`
- Test: `tests/Diariz.Api.Tests/AuthControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `AuthControllerTests.cs`. These mint a code in the fake bound to `OAuthPkce.Challenge(verifier)`, then exercise success + each failure mode:

```csharp
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
```

`OAuthPkce` is in `Diariz.Api.Services`; ensure `using Diariz.Api.Services;` is present in the test file (added in Task 3).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name~DesktopExchange"`
Expected: FAIL to compile (`DesktopExchange` method doesn't exist).

- [ ] **Step 3: Implement the endpoint**

Add to `AuthController.cs`, next to the web `GoogleExchange`:

```csharp
    /// <summary>Public: the desktop app swaps its one-time diariz:// code for an access token, proving it
    /// holds the PKCE verifier whose S256 challenge was bound to the code. Any failure -> generic 401.</summary>
    [HttpPost("desktop/exchange")]
    public async Task<IActionResult> DesktopExchange(DesktopExchangeRequest req)
    {
        if (string.IsNullOrEmpty(req.Code) || string.IsNullOrEmpty(req.Verifier)) return Unauthorized();

        var ticket = await _desktopCodes.RedeemAsync(req.Code);
        if (ticket is null || !FixedTimeEquals(ticket.Challenge, OAuthPkce.Challenge(req.Verifier)))
            return Unauthorized();

        var user = await _users.FindByIdAsync(ticket.UserId.ToString());
        if (user is null || !user.IsEnabled) return Unauthorized();

        var (token, _) = _tokens.CreateAccessToken(user, await _users.GetRolesAsync(user));
        return Ok(new { accessToken = token });
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name~DesktopExchange"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/AuthController.cs tests/Diariz.Api.Tests/AuthControllerTests.cs
git commit -m "feat(api): POST desktop/exchange swaps one-time code for a token"
```

---

### Task 6: PR 1 version bump, docs, and full suite

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `apps/desktop/package-lock.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the version mirrors to `0.97.9`** (rebase if `main` advanced)

Set `version.json` -> `{ "version": "0.97.9" }`. Set `"version": "0.97.9"` in `apps/web/package.json` and `apps/desktop/package.json`. Set `<Version>0.97.9</Version>` in `src/Diariz.Api/Diariz.Api.csproj`. Then:

Run: `cd apps/desktop && npm i --package-lock-only && cd ../..`
Expected: `apps/desktop/package-lock.json` version fields update to `0.97.9`.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` (above the current `[0]`):

```ts
  {
    version: "0.97.9",
    date: "2026-07-06",
    pr: 0, // set to the real PR number when opened
    headline: "Groundwork for Google sign-in on the desktop app",
    summary:
      "Backend groundwork (no user-visible change yet). Adds a server-side handoff so the desktop app " +
      "can complete Google sign-in via the system browser: the Google callback can hand back a single-use " +
      "code over a diariz:// deep link, which the app exchanges for a session. Ships fully in a later update.",
    added: [
      "Server support for a desktop Google sign-in handoff (single-use code + diariz:// deep link).",
    ],
  },
```

- [ ] **Step 3: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, in the Google/auth section, add a sentence documenting the new cross-boundary contract: the desktop shell starts Google sign-in in the system browser with a `desktopChallenge`; the callback mints a single-use Redis code and 302s to `diariz://auth/callback?code=…`; the app redeems it at `POST /api/auth/desktop/exchange` with the PKCE verifier. (No schema change; no `Data_Schema.md` edit - the code lives only in Redis.)

- [ ] **Step 4: Run the full backend + web suites**

Run: `dotnet test tests/Diariz.Api.Tests && cd apps/web && npx vitest run && cd ..`
Expected: all green (`releases.test.ts` asserts `RELEASES[0].version === version.json`).

- [ ] **Step 5: Commit + open PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts docs/Overall_Synopsis_of_Platform.md
git commit -m "chore: release 0.97.9 - desktop Google sign-in API groundwork"
git push -u origin feat/desktop-google-signin-api
gh pr create --base main --title "Desktop Google sign-in: API groundwork (0.97.9)" --body "First of three PRs. Server redeploy; no desktop release; no migration (Redis-backed code). See docs/superpowers/plans/2026-07-06-desktop-google-signin.md."
```

Then update the `pr:` number in `releases.ts` to the real number, commit, and push (per the repo's version-mirror discipline).

---

## PR 2 - Desktop shell: protocol registration, deep-link handling, IPC

Branch (off updated `main` after PR 1 merges): `feat/desktop-google-signin-shell`

### Task 7: Pure `desktopAuth.js` helpers (URL build + deep-link parse)

**Files:**
- Create: `apps/desktop/src/desktopAuth.js`
- Test: `apps/desktop/src/desktopAuth.test.js`

- [ ] **Step 1: Write the failing test**

Create `apps/desktop/src/desktopAuth.test.js`:

```javascript
"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { buildStartUrl, codeFromArgv } = require("./desktopAuth");

test("buildStartUrl points at the server's Google start with the desktop challenge", () => {
  const url = buildStartUrl("https://diariz.example.com", "CHALLENGE123");
  assert.equal(url, "https://diariz.example.com/api/auth/google/start?desktopChallenge=CHALLENGE123");
});

test("buildStartUrl trims a trailing slash on the server origin", () => {
  const url = buildStartUrl("https://diariz.example.com/", "abc");
  assert.equal(url, "https://diariz.example.com/api/auth/google/start?desktopChallenge=abc");
});

test("buildStartUrl url-encodes the challenge", () => {
  const url = buildStartUrl("https://x.test", "a b+c");
  assert.equal(url, "https://x.test/api/auth/google/start?desktopChallenge=a%20b%2Bc");
});

test("codeFromArgv extracts the code from a diariz:// deep link anywhere in argv", () => {
  assert.equal(codeFromArgv(["app.exe", "diariz://auth/callback?code=THE_CODE"]), "THE_CODE");
  assert.equal(codeFromArgv(["diariz://auth/callback?code=abc&x=1"]), "abc");
});

test("codeFromArgv url-decodes the code", () => {
  assert.equal(codeFromArgv(["diariz://auth/callback?code=a%2Bb"]), "a+b");
});

test("codeFromArgv returns null for junk / no code / wrong host", () => {
  assert.equal(codeFromArgv(["app.exe", "--flag"]), null);
  assert.equal(codeFromArgv(["diariz://auth/callback"]), null);
  assert.equal(codeFromArgv(["diariz://other/path?code=x"]), null);
  assert.equal(codeFromArgv([]), null);
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd apps/desktop && node --test src/desktopAuth.test.js`
Expected: FAIL (`Cannot find module './desktopAuth'`).

- [ ] **Step 3: Implement the helpers**

Create `apps/desktop/src/desktopAuth.js`:

```javascript
"use strict";

// Pure helpers for the desktop Google sign-in deep-link flow. Kept free of Electron so they can be
// unit-tested with `node --test` (same pattern as recorderState.js / updateState.js). Crypto
// (verifier/challenge generation) lives in main.js; only the string/URL shaping is here.

const DEEP_LINK_PREFIX = "diariz://auth/callback";

/// Build the API's Google-start URL for a desktop flow: carries the S256 PKCE challenge so the
/// server marks the flow as desktop and hands back a code instead of the SPA cookie.
function buildStartUrl(serverOrigin, challenge) {
  const base = String(serverOrigin || "").replace(/\/+$/, "");
  return `${base}/api/auth/google/start?desktopChallenge=${encodeURIComponent(challenge)}`;
}

/// Extract the one-time code from a diariz://auth/callback?code=… deep link found anywhere in an
/// argv array (Windows delivers the URL as a process argument). Returns null if absent/malformed.
function codeFromArgv(argv) {
  for (const arg of argv || []) {
    if (typeof arg !== "string" || !arg.startsWith(DEEP_LINK_PREFIX)) continue;
    const q = arg.indexOf("?");
    if (q === -1) continue;
    const code = new URLSearchParams(arg.slice(q + 1)).get("code");
    if (code) return code;
  }
  return null;
}

module.exports = { buildStartUrl, codeFromArgv };
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd apps/desktop && node --test src/desktopAuth.test.js`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/desktopAuth.js apps/desktop/src/desktopAuth.test.js
git commit -m "feat(desktop): pure helpers for the Google sign-in deep link"
```

---

### Task 8: Register the `diariz://` protocol (installer + runtime)

**Files:**
- Modify: `apps/desktop/electron-builder.config.js`
- Modify: `apps/desktop/src/main.js`

No automated test (Electron/installer glue); Step 4 is a manual dev check.

- [ ] **Step 1: Declare the protocol for the installer**

In `apps/desktop/electron-builder.config.js`, add a top-level `protocols` entry to the exported config (alongside `appId`, `productName`):

```javascript
module.exports = {
  appId: "com.diariz.desktop",
  productName: "Diariz",
  protocols: [{ name: "Diariz", schemes: ["diariz"] }],
  directories: { output: "release", buildResources: "build" },
```

- [ ] **Step 2: Register the scheme at runtime**

In `apps/desktop/src/main.js`, inside the `else` branch of the single-instance block (right after `app.setAppUserModelId("com.diariz.desktop");`), register as the default handler. Use the exec-path form when unpackaged so `npm start` dev also works:

```javascript
    app.setAppUserModelId("com.diariz.desktop");

    // Own the diariz:// scheme so Google sign-in deep links come back to this app. In dev (unpackaged)
    // Windows needs the explicit exec path + script arg; packaged builds register it via the installer.
    if (process.defaultApp && process.argv.length >= 2) {
      app.setAsDefaultProtocolClient("diariz", process.execPath, [path.resolve(process.argv[1])]);
    } else {
      app.setAsDefaultProtocolClient("diariz");
    }
```

- [ ] **Step 3: Build to verify it packages**

Run: `cd apps/desktop && npm test`
Expected: PASS (existing `node --test` suite unaffected; this step just confirms nothing broke).

- [ ] **Step 4 (manual, note in PR): dev protocol check**

In a packaged or dev run, opening `diariz://auth/callback?code=test` from the OS should focus Diariz. This is verified during the manual smoke test (Task 11), not in CI.

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/electron-builder.config.js apps/desktop/src/main.js
git commit -m "feat(desktop): register the diariz:// custom protocol"
```

---

### Task 9: Wire the sign-in IPC + deep-link delivery in main.js and preload.js

**Files:**
- Modify: `apps/desktop/src/main.js`
- Modify: `apps/desktop/src/preload.js`

No new automated test (the pure parts are covered in Task 7; this is Electron glue). Step 5 runs the existing suite to confirm no regressions.

- [ ] **Step 1: Import the helpers + Node crypto in main.js**

At the top of `apps/desktop/src/main.js`, add to the requires:

```javascript
const path = require("node:path");
const crypto = require("node:crypto");
const { app, BrowserWindow, Tray, Menu, Notification, desktopCapturer, ipcMain, shell, nativeImage } = require("electron");
const Store = require("electron-store");
const { normalizeServerUrl } = require("./url");
const { trayRecorderItems, trayTooltip, notificationFor } = require("./recorderState");
const { updateRestartItem, notificationForUpdate } = require("./updateState");
const { buildStartUrl, codeFromArgv } = require("./desktopAuth");
```

- [ ] **Step 2: Add the sign-in flow + deep-link handler in main.js**

Add a module-scoped pending verifier near the other `let` state (after `let update = …`):

```javascript
let pendingVerifier = null;
```

Add these functions (e.g. after the `// ---- Tray-driven recording ----` section, before `// ---- Auto-update`):

```javascript
// ---- Desktop Google sign-in (system browser + diariz:// deep link) ----

// base64url(sha256(verifier)) - matches the API's OAuthPkce.Challenge (ASCII verifier, no padding).
function s256(verifier) {
  return crypto.createHash("sha256").update(verifier, "ascii").digest("base64url");
}

// Renderer asked to start Google sign-in: generate PKCE, open the server's start URL in the SYSTEM
// browser (Google refuses embedded webviews), and keep the verifier to redeem the code later.
function startGoogleSignIn() {
  const server = targetUrl();
  if (!server) return;
  const verifier = crypto.randomBytes(32).toString("base64url");
  pendingVerifier = verifier;
  const origin = new URL(server).origin;
  shell.openExternal(buildStartUrl(origin, s256(verifier)));
}

// A diariz:// deep link arrived (argv on cold start, or the second-instance event). Redeem the code
// for a token and hand it to the renderer; then surface the window.
async function handleAuthDeepLink(argv) {
  const code = codeFromArgv(argv);
  if (!code || !pendingVerifier) return;
  const verifier = pendingVerifier;
  pendingVerifier = null;
  const server = targetUrl();
  if (!server) return;
  try {
    const res = await fetch(`${new URL(server).origin}/api/auth/desktop/exchange`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code, verifier }),
    });
    if (!res.ok) return;
    const { accessToken } = await res.json();
    if (accessToken) deliverAuthToken(accessToken);
  } catch {
    // network/other error: leave the user on the login screen to retry
  }
}

// Send the token to the renderer, waiting for the page to finish loading on a cold start.
function deliverAuthToken(token) {
  showMainWindow();
  if (!mainWindow) return;
  const wc = mainWindow.webContents;
  if (wc.isLoading()) wc.once("did-finish-load", () => wc.send("auth:token", token));
  else wc.send("auth:token", token);
  mainWindow.show();
  mainWindow.focus();
}
```

Wire the IPC handler (near the other `ipcMain` registrations, e.g. after `ipcMain.on("recorder:state", …)`):

```javascript
ipcMain.handle("auth:start-google", () => startGoogleSignIn());
```

In the single-instance `else` block, deliver deep links from both entry points. Change the existing `second-instance` handler and add a cold-start parse:

```javascript
    app.on("second-instance", (_e, argv) => {
      showMainWindow();
      void handleAuthDeepLink(argv);
    });

    app.whenReady().then(() => {
      Menu.setApplicationMenu(null);
      buildTray();
      if (targetUrl()) createMainWindow(targetUrl());
      else showSetupWindow();

      setupAutoUpdater();
      void handleAuthDeepLink(process.argv); // cold start launched by a deep link
      app.on("activate", () => showMainWindow());
    });
```

(Also add a macOS `open-url` handler for future portability - harmless on Windows. Place it in the `else` block:)

```javascript
    app.on("open-url", (e, url) => {
      e.preventDefault();
      void handleAuthDeepLink([url]);
    });
```

- [ ] **Step 3: Expose the two methods in preload.js**

In `apps/desktop/src/preload.js`, add to the `exposeInMainWorld("diariz", { … })` object:

```javascript
  /// Report the recorder phase to the main process so the tray can update.
  /// state: { phase: "idle"|"recording"|"uploading"|"error", source?, error? }.
  reportRecorderState: (state) => ipcRenderer.send("recorder:state", state),

  /// Start Google sign-in (opens the system browser; the result returns via onAuthToken).
  startGoogleSignIn: () => ipcRenderer.invoke("auth:start-google"),

  /// Subscribe to a signed-in access token delivered after a diariz:// sign-in deep link.
  /// Returns an unsubscribe function.
  onAuthToken: (cb) => {
    const listener = (_event, token) => cb(token);
    ipcRenderer.on("auth:token", listener);
    return () => ipcRenderer.removeListener("auth:token", listener);
  },
```

- [ ] **Step 4: Run the desktop suite**

Run: `cd apps/desktop && npm test`
Expected: PASS (existing + Task 7 tests; the new glue has no unit tests but must not break the suite).

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/main.js apps/desktop/src/preload.js
git commit -m "feat(desktop): system-browser Google sign-in + diariz:// token handoff"
```

---

### Task 10: PR 2 version bump + docs

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `apps/desktop/package-lock.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Bump the mirrors to `0.97.10`** (rebase if needed)

Update `version.json`, both `package.json`s, and the `.csproj` `<Version>` to `0.97.10`, then:

Run: `cd apps/desktop && npm i --package-lock-only && cd ../..`
Expected: lock file updated.

- [ ] **Step 2: Add the release entry** (mark it as needing a desktop release)

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.97.10",
    date: "2026-07-06",
    pr: 0, // set to the real PR number when opened
    headline: "Desktop shell support for Google sign-in",
    summary:
      "Desktop app update (new installer). Registers the diariz:// link so the desktop client can finish " +
      "Google sign-in through your system browser and return you to the app. The sign-in button appears in " +
      "the next update. Requires installing the new desktop build.",
    added: [
      "Desktop app registers the diariz:// protocol and handles the Google sign-in return link.",
    ],
  },
```

- [ ] **Step 3: Run web tests**

Run: `cd apps/web && npx vitest run && cd ..`
Expected: green (`releases.test.ts` passes with the bumped version).

- [ ] **Step 4: Commit + open PR (flag the desktop release)**

```bash
git add version.json apps/web/package.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore: release 0.97.10 - desktop shell Google sign-in support"
git push -u origin feat/desktop-google-signin-shell
gh pr create --base main --title "Desktop Google sign-in: shell + protocol (0.97.10)" --body "Second of three PRs. DESKTOP RELEASE REQUIRED (new installer registers diariz://) - cut a v* tag after merge. Also server-redeployable web/API mirrors. See the plan doc."
```

Update the `pr:` number in `releases.ts` after the PR opens, commit, push.

---

## PR 3 - Web: surface the Google button in Electron + intake the token

Branch (off updated `main` after PR 2 merges): `feat/desktop-google-signin-web`

### Task 11: `GoogleSignInButton` supports a button variant

**Files:**
- Modify: `apps/web/src/components/GoogleSignInButton.tsx`
- Test: `apps/web/src/components/GoogleSignInButton.test.tsx` (create)

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/GoogleSignInButton.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import GoogleSignInButton from "./GoogleSignInButton";

describe("GoogleSignInButton", () => {
  it("renders an anchor to the server start endpoint by default (web)", () => {
    render(<GoogleSignInButton label="Sign in with Google" />);
    const link = screen.getByRole("link", { name: /sign in with google/i });
    expect(link.getAttribute("href")).toBe("/api/auth/google/start");
  });

  it("renders a button that calls onClick when provided (desktop)", () => {
    const onClick = vi.fn();
    render(<GoogleSignInButton label="Sign in with Google" onClick={onClick} />);
    expect(screen.queryByRole("link")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /sign in with google/i }));
    expect(onClick).toHaveBeenCalledOnce();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd apps/web && npx vitest run src/components/GoogleSignInButton.test.tsx`
Expected: FAIL (button variant not implemented; second test finds no button).

- [ ] **Step 3: Implement the optional `onClick` variant**

Replace the component in `apps/web/src/components/GoogleSignInButton.tsx` (keep `GoogleLogo` unchanged below it):

```tsx
/// "Sign in with Google" button. Default (web) is a plain link (full-page navigation) to the API's
/// server-side OAuth start endpoint. When `onClick` is given (desktop shell) it renders a button
/// instead - the desktop flow opens the system browser via IPC, since an in-window navigation to
/// Google would be ejected by the shell and lose the flow.
const CLASS =
  "flex w-full items-center justify-center gap-2 rounded border py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800";

export default function GoogleSignInButton({ label, onClick }: { label: string; onClick?: () => void }) {
  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={CLASS}>
        <GoogleLogo />
        {label}
      </button>
    );
  }
  return (
    <a href="/api/auth/google/start" className={CLASS}>
      <GoogleLogo />
      {label}
    </a>
  );
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd apps/web && npx vitest run src/components/GoogleSignInButton.test.tsx`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/GoogleSignInButton.tsx apps/web/src/components/GoogleSignInButton.test.tsx
git commit -m "feat(web): GoogleSignInButton supports a desktop button variant"
```

---

### Task 12: Show the Google button in Electron and start the IPC flow

**Files:**
- Modify: `apps/web/src/pages/Login.tsx`
- Test: `apps/web/src/pages/Login.test.tsx` (create)

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/pages/Login.test.tsx`. It mocks `../lib/audioSource` to force `isElectron: true` and stubs the auth/api/i18n deps:

```tsx
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/audioSource", () => ({ isElectron: true }));
vi.mock("../auth", () => ({ useAuth: () => ({ login: vi.fn() }) }));
vi.mock("../lib/api", () => ({
  api: { getAuthProviders: vi.fn().mockResolvedValue({ google: true }) },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}));

import Login from "./Login";

function renderLogin() {
  return render(
    <MemoryRouter>
      <Login />
    </MemoryRouter>,
  );
}

describe("Login (Electron)", () => {
  beforeEach(() => {
    (window as unknown as { diariz: unknown }).diariz = { startGoogleSignIn: vi.fn() };
  });

  it("shows the Google button in the desktop shell and starts sign-in via IPC", async () => {
    renderLogin();
    // The button (not an anchor) is present once providers resolve with google:true.
    const btn = await screen.findByRole("button", { name: /signInWithGoogle/i });
    fireEvent.click(btn);
    await waitFor(() =>
      expect((window as unknown as { diariz: { startGoogleSignIn: ReturnType<typeof vi.fn> } }).diariz.startGoogleSignIn)
        .toHaveBeenCalledOnce(),
    );
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd apps/web && npx vitest run src/pages/Login.test.tsx`
Expected: FAIL - today the Google block is hidden when `isElectron` is true, so no button renders.

- [ ] **Step 3: Implement the Electron branch in Login.tsx**

In `apps/web/src/pages/Login.tsx`, update the comment (lines ~21-22) and the Google block (lines ~104-113). Replace the comment:

```tsx
  // Google sign-in is offered when the server has it configured. In the Electron shell it runs through
  // the system browser (via IPC) because Google blocks OAuth in embedded webviews; on the web it is a
  // normal full-page redirect.
```

Replace the conditional block:

```tsx
        {googleEnabled && (
          <>
            <div className="flex items-center gap-2 text-xs text-gray-400">
              <span className="h-px flex-grow bg-gray-200 dark:bg-gray-700" />
              {t("or")}
              <span className="h-px flex-grow bg-gray-200 dark:bg-gray-700" />
            </div>
            <GoogleSignInButton
              label={t("signInWithGoogle")}
              onClick={isElectron ? () => window.diariz?.startGoogleSignIn() : undefined}
            />
          </>
        )}
```

If TypeScript complains about `window.diariz`, add a minimal ambient type. Check whether `apps/web/src` already declares `window.diariz` (search for `interface Window`); if not, create `apps/web/src/diariz.d.ts`:

```ts
export {};
declare global {
  interface Window {
    diariz?: {
      isElectron?: boolean;
      startGoogleSignIn?: () => void;
      onAuthToken?: (cb: (token: string) => void) => () => void;
    };
  }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd apps/web && npx vitest run src/pages/Login.test.tsx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/pages/Login.tsx apps/web/src/diariz.d.ts
git commit -m "feat(web): show Google sign-in in Electron, start it over IPC"
```

---

### Task 13: AuthProvider adopts a token delivered by the desktop shell

**Files:**
- Modify: `apps/web/src/auth.tsx`
- Test: `apps/web/src/auth.test.tsx` (create)

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/auth.test.tsx`:

```tsx
import { render, screen, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("./lib/api", () => ({
  api: { refresh: vi.fn() },
  getToken: vi.fn(() => null),
  setToken: vi.fn(),
}));

import { AuthProvider, useAuth } from "./auth";

function Probe() {
  const { isAuthed } = useAuth();
  return <div>{isAuthed ? "authed" : "anon"}</div>;
}

describe("AuthProvider desktop token intake", () => {
  let handler: ((token: string) => void) | null = null;
  beforeEach(() => {
    handler = null;
    (window as unknown as { diariz: unknown }).diariz = {
      onAuthToken: (cb: (token: string) => void) => {
        handler = cb;
        return () => {};
      },
    };
  });

  it("adopts a token pushed by the desktop shell", () => {
    // A valid-looking JWT (header.payload.signature); payload has a future exp so it parses as authed.
    const payload = btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 3600 }));
    const token = `eyJhbGciOiJIUzI1NiJ9.${payload}.sig`;

    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );
    expect(screen.getByText("anon")).toBeTruthy();

    act(() => handler!(token));
    expect(screen.getByText("authed")).toBeTruthy();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd apps/web && npx vitest run src/auth.test.tsx`
Expected: FAIL - AuthProvider does not subscribe to `onAuthToken`, so the probe stays "anon".

- [ ] **Step 3: Implement the subscription**

In `apps/web/src/auth.tsx`, add an effect inside `AuthProvider` (after the existing refresh `useEffect`, before the `return`). It subscribes only when the desktop bridge is present:

```tsx
  // Desktop shell: after a diariz:// Google sign-in, the shell pushes the access token here. Adopt it
  // through the same path as a normal login (persist + schedule refresh). No-op in a plain browser.
  useEffect(() => {
    const unsub = window.diariz?.onAuthToken?.((incoming) => setSession(incoming));
    return () => unsub?.();
  }, []);
```

Ensure `useEffect` is imported (it already is). The `window.diariz` type comes from `apps/web/src/diariz.d.ts` (Task 12).

- [ ] **Step 4: Run to verify it passes**

Run: `cd apps/web && npx vitest run src/auth.test.tsx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/auth.tsx apps/web/src/auth.test.tsx
git commit -m "feat(web): AuthProvider adopts a desktop-delivered Google token"
```

---

### Task 14: PR 3 version bump, About-box/docs copy, full verification

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `apps/desktop/package-lock.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`, `README.md`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the mirrors to `0.98.0`** (Minor +1 - the feature goes live; rebase if needed)

Update `version.json`, both `package.json`s, and the `.csproj` `<Version>` to `0.98.0`, then:

Run: `cd apps/desktop && npm i --package-lock-only && cd ../..`

- [ ] **Step 2: Add the release entry + update the About-box capability line**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.98.0",
    date: "2026-07-06",
    pr: 0, // set to the real PR number when opened
    headline: "Sign in with Google from the desktop app",
    summary:
      "The desktop app now offers Sign in with Google. It opens Google in your system browser (Google " +
      "does not allow sign-in inside app windows) and brings you straight back into Diariz. This unblocks " +
      "Google-only accounts, which previously could not use the desktop app at all. Requires the desktop " +
      "build from the previous update (which registers the return link).",
    added: [
      "Sign in with Google in the desktop client (system-browser flow, returns via a secure one-time link).",
    ],
  },
```

In the same file, find the `CAPABILITIES` string's MCP/desktop area and adjust the copy that says desktop uses password login, so it reflects that Google sign-in now works on desktop (search `CAPABILITIES` for "sign in with Google" and "desktop"; add a phrase that Google sign-in is available in the desktop app via the system browser).

- [ ] **Step 3: Update README + synopsis**

In `README.md`, update the Features/desktop wording to note Google sign-in works in the desktop app. In `docs/Overall_Synopsis_of_Platform.md`, complete the desktop-OAuth contract note (the web button + AuthProvider intake) started in PR 1.

- [ ] **Step 4: Full verification**

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS.

Run: `cd apps/web && npm run build && npx vitest run && cd ..`
Expected: build clean; all web tests pass (incl. `releases.test.ts` + `locales.test.ts`).

Run: `cd apps/desktop && npm test && cd ../..`
Expected: PASS.

- [ ] **Step 5: Commit + open PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts README.md docs/Overall_Synopsis_of_Platform.md
git commit -m "feat: sign in with Google from the desktop app (0.98.0)"
git push -u origin feat/desktop-google-signin-web
gh pr create --base main --title "Desktop Google sign-in: web surface (0.98.0)" --body "Third of three PRs - surfaces the feature. Server redeploy. Requires the desktop build from PR 2 (registers diariz://) to complete the round trip. See the plan doc."
```

Update the `pr:` number in `releases.ts` after the PR opens, commit, push.

---

## Post-merge manual smoke test (user)

After all three PRs merge, the server is redeployed, and a new desktop build (`v*` tag) is installed:

1. Install the new desktop app (registers `diariz://`).
2. On the desktop login screen, a **Sign in with Google** button appears; click it.
3. The **system browser** opens Google consent; approve.
4. The browser returns to Diariz and the desktop app **comes to the foreground, signed in** as the Google user.
5. Confirm a **Google-only** account (no password) can now sign in on desktop.

## Self-review notes (author)

- **Spec coverage:** `OAuthState.DesktopChallenge` (Task 3), `/start` marker (Task 3), desktop callback + Redis code (Tasks 1, 4), `/desktop/exchange` (Task 5), `IDesktopAuthCodeStore` + fake + integration test (Tasks 1-2), protocol registration (Task 8), deep-link handling + IPC + preload (Tasks 7, 9), `Login` button + `AuthProvider` intake (Tasks 11-13), security properties (enforced by Tasks 4-5 + `s256` in Task 9), testing across all three stacks, deploy/versioning (Tasks 6, 10, 14). All spec sections map to tasks.
- **Type consistency:** `MintAsync(Guid, string, TimeSpan)` / `RedeemAsync(string) -> DesktopAuthTicket?` used identically in the interface, Redis impl, fake, controller, and tests. `buildStartUrl`/`codeFromArgv`/`s256`/`startGoogleSignIn`/`onAuthToken`/`auth:token`/`auth:start-google` names are consistent across main.js, preload.js, and the web side.
- **No placeholders** except the intentional `pr: 0` (resolved when each PR opens, per the repo's version-mirror workflow).
