# Integrations Phase 1: Foundation & Inbound Hardening - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three granular platform toggles (API / MCP / Webhooks), give personal `dz_api_` tokens a read-only/read-write scope and optional expiry, verify+document the existing formula fire/fetch REST endpoints, and fix the two in-app API-reference links to open in a new tab.

**Architecture:** Extend two existing entities (`ApiAccessToken`, `PlatformSettings`) with additive columns and one migration each. Enforce token expiry + read-only in the existing `dz_api_` auth path (authenticator + a new global write-block middleware). Mirror the existing `ApiAccessEnabled` kill-switch pattern for a new `McpAccessEnabled` gate. All changes are backward compatible (existing tokens stay full-access, MCP stays enabled).

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit (unit + Testcontainers integration), React 19 + TS + Vite + Tailwind v4, vitest, i18next (en/de/es/fr).

This is **Phase 1 of 3**. Phase 2 (outbound webhooks core) and Phase 3 (Workflow Signals + platform routing) follow as separate plans/PRs. Phase 1 ships working software on its own.

Source spec: `docs/superpowers/specs/2026-07-23-integrations-design.md` (sections 6, 11, 14).

## Global Constraints

- **TDD:** write the failing test first, watch it fail, then the minimal code. No production code without a preceding red test.
- **No em/en dashes in user-facing copy** (UI strings, all four locale catalogs, release notes). Plain hyphen `-` only. Code/comments/internal docs are exempt.
- **Backward compatibility (critical):** existing `ApiAccessToken` rows must remain **ReadWrite** and **never-expire**; the existing `PlatformSettings` singleton must remain **MCP-enabled**. The migrations must set `Scope` default `1` (ReadWrite) and `McpAccessEnabled` default `true` explicitly - EF will not infer these.
- **Platform toggle semantics:** each admin toggle is bounded by its env kill-switch. `ApiAccessEnabled` (default off, unchanged), `McpAccessEnabled` (default **on**), `WebhooksEnabled` (default off).
- **i18n:** every new user-facing key is added to the same namespace file in **all four** languages `en/de/es/fr` under `apps/web/src/locales/<lang>/`. API/integration strings live in `account.json`.
- **Versioning:** this PR is a functional enhancement -> Minor bump `0.150.0` -> `0.151.0`, mirrored to `version.json` + `apps/web/package.json` + `apps/desktop/package.json` + `src/Diariz.Api/Diariz.Api.csproj`, with `RELEASES[0]` (in `apps/web/src/lib/releases.ts`) equal to `version.json`.
- **Deployment surface:** server redeploy only (no desktop release). Migrations are additive and forward-restore-safe -> no `MaintenanceController.CurrentFormat` bump.

---

## File Structure

**Backend - create:**
- `src/Diariz.Domain/Entities/ApiTokenScope.cs` - the `ReadOnly`/`ReadWrite` enum.
- `src/Diariz.Api/Auth/ApiTokenScopeMiddleware.cs` - global middleware + the pure `ApiTokenScopePolicy.BlocksWrite` predicate that 403s an unsafe verb from a read-only token.
- `src/Diariz.Domain/Migrations/<ts>_AddApiTokenScopeExpiry.cs` - generated migration (edited for defaults).
- `src/Diariz.Domain/Migrations/<ts>_AddPlatformIntegrationToggles.cs` - generated migration (edited for defaults).

**Backend - modify:**
- `src/Diariz.Domain/Entities/ApiAccessToken.cs` - add `Scope`, `ExpiresAt`.
- `src/Diariz.Domain/Entities/PlatformSettings.cs` - add `McpAccessEnabled`, `WebhooksEnabled`.
- `src/Diariz.Api/Services/ApiTokenAuthenticator.cs` - return scope, enforce expiry.
- `src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs` - emit the scope claim.
- `src/Diariz.Api/Services/McpTokenAuthenticator.cs` OR `src/Diariz.Api/Auth/McpBearerAuthenticationHandler.cs` - gate on `McpAccessEnabled`.
- `src/Diariz.Api/Controllers/ApiTokensController.cs` - accept scope + expiry on create; expose them on list.
- `src/Diariz.Api/Controllers/PlatformSettingsController.cs` - read/write the two new toggles.
- `src/Diariz.Api/Contracts/ApiDtos.cs` - extend token + platform-settings DTOs.
- `src/Diariz.Api/Program.cs` - register `ApiTokenScopeMiddleware` after `UseAuthentication`.

**Web - modify:**
- `apps/web/src/lib/types.ts` - `PlatformSettings`, `ApiToken`, `ApiTokenCreated`, plus a `CreateApiTokenOptions` type.
- `apps/web/src/lib/api.ts` - `createApiToken` takes options; platform-settings type flows through.
- `apps/web/src/components/DeveloperAccessSection.tsx` - read-only checkbox + optional expiry.
- `apps/web/src/components/SettingsModal.tsx` - two new toggles wired through load/save.
- `apps/web/src/locales/{en,de,es,fr}/account.json` - new strings.
- The two API-reference links: `SettingsModal.tsx` + `DeveloperAccessSection.tsx` open `/developers/api` in a new tab.

**Docs / version - modify:**
- `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`.
- `apps/web/src/lib/releases.ts` (RELEASES entry + CAPABILITIES rows).
- `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`.

---

## Task 1: `ApiAccessToken` gains Scope + ExpiresAt (schema)

**Files:**
- Create: `src/Diariz.Domain/Entities/ApiTokenScope.cs`
- Modify: `src/Diariz.Domain/Entities/ApiAccessToken.cs`
- Create: `src/Diariz.Domain/Migrations/<ts>_AddApiTokenScopeExpiry.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/ApiTokenSchemaTests.cs`

**Interfaces:**
- Produces: `enum ApiTokenScope { ReadOnly = 0, ReadWrite = 1 }`; `ApiAccessToken.Scope` (default `ReadWrite`), `ApiAccessToken.ExpiresAt` (`DateTimeOffset?`).

- [ ] **Step 1: Write the failing test** (a real-Postgres round-trip proves the columns + defaults exist)

Create `tests/Diariz.Api.IntegrationTests/ApiTokenSchemaTests.cs`:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class ApiTokenSchemaTests
{
    private readonly ContainersFixture _fx;
    public ApiTokenSchemaTests(ContainersFixture fx) => _fx = fx;

    [Fact]
    public async Task Token_persists_scope_and_expiry_and_defaults_to_readwrite()
    {
        await using var db = _fx.CreateDbContext();
        var userId = await SeedUser.CreateAsync(db);

        var expires = DateTimeOffset.UtcNow.AddDays(30);
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "scoped", TokenHash = Guid.NewGuid().ToString("N"),
            Prefix = "dz_api_aaa", Scope = ApiTokenScope.ReadOnly, ExpiresAt = expires,
        });
        // A token created without setting Scope must default to ReadWrite (backward compatible).
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "default", TokenHash = Guid.NewGuid().ToString("N"),
            Prefix = "dz_api_bbb",
        });
        await db.SaveChangesAsync();

        var scoped = await db.ApiAccessTokens.SingleAsync(t => t.Name == "scoped");
        var def = await db.ApiAccessTokens.SingleAsync(t => t.Name == "default");
        Assert.Equal(ApiTokenScope.ReadOnly, scoped.Scope);
        Assert.NotNull(scoped.ExpiresAt);
        Assert.Equal(ApiTokenScope.ReadWrite, def.Scope);
        Assert.Null(def.ExpiresAt);
    }
}
```

> If `SeedUser` does not exist in the integration project, create a minimal owner inline instead (insert an `ApplicationUser` with `Id`, `Email`, `UserName`) - follow whatever the sibling integration tests use to seed a user.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ApiTokenSchemaTests"`
Expected: FAIL to compile (`ApiTokenScope` and `Scope`/`ExpiresAt` do not exist).

- [ ] **Step 3: Create the enum**

`src/Diariz.Domain/Entities/ApiTokenScope.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Coarse capability of a personal API token. Append only - values persist as ints.
/// New tokens choose one at mint time; existing tokens default to <see cref="ReadWrite"/>.</summary>
public enum ApiTokenScope
{
    /// <summary>Safe verbs only (GET/HEAD). Unsafe verbs (POST/PUT/PATCH/DELETE) are rejected with 403.</summary>
    ReadOnly = 0,

    /// <summary>Full access - the historical behaviour of every token.</summary>
    ReadWrite = 1,
}
```

- [ ] **Step 4: Add the fields to the entity**

In `src/Diariz.Domain/Entities/ApiAccessToken.cs`, add after `LastUsedAt`:

```csharp
    /// <summary>Coarse capability. Defaults to <see cref="ApiTokenScope.ReadWrite"/> so pre-existing tokens
    /// keep full access; the migration sets the column default to 1 for the same reason.</summary>
    public ApiTokenScope Scope { get; set; } = ApiTokenScope.ReadWrite;

    /// <summary>Optional hard expiry. Null = never expires (all pre-existing tokens).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
```

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add AddApiTokenScopeExpiry --project src/Diariz.Domain --startup-project src/Diariz.Api`

- [ ] **Step 6: Fix the generated migration's Scope default** (EF will emit no default -> existing rows would become `ReadOnly`)

Open the generated `..._AddApiTokenScopeExpiry.cs`. Ensure the `Up` adds `Scope` with `defaultValue: 1`:

```csharp
migrationBuilder.AddColumn<int>(
    name: "Scope", table: "ApiAccessTokens", type: "integer", nullable: false, defaultValue: 1);
migrationBuilder.AddColumn<DateTimeOffset>(
    name: "ExpiresAt", table: "ApiAccessTokens", type: "timestamp with time zone", nullable: true);
```

`Down` drops both columns.

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ApiTokenSchemaTests"`
Expected: PASS (containers apply the migration, round-trip succeeds).

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/ApiTokenScope.cs src/Diariz.Domain/Entities/ApiAccessToken.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/ApiTokenSchemaTests.cs
git commit -m "feat: add Scope and ExpiresAt to API tokens (schema)"
```

---

## Task 2: Enforce token expiry + surface scope in the auth path

**Files:**
- Modify: `src/Diariz.Api/Services/ApiTokenAuthenticator.cs`
- Modify: `src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokenAuthenticatorTests.cs`

**Interfaces:**
- Produces: `record ApiTokenAuth(Guid UserId, ApiTokenScope Scope)`; `IApiTokenAuthenticator.AuthenticateAsync(...) : Task<ApiTokenAuth?>` (was `Task<Guid?>`); `ApiKeyAuthenticationHandler.ScopeClaimType = "diariz:api_scope"` (claim value = `scope.ToString()`).
- Consumes: `ApiTokenScope` (Task 1); `IPlatformSettingsService.GetAsync` (existing).

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/ApiTokenAuthenticatorTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenAuthenticatorTests
{
    private static (DiarizDbContext db, Guid userId, string token) Seed(
        ApiTokenScope scope, DateTimeOffset? expiresAt, bool apiEnabled = true)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, ApiAccessEnabled = apiEnabled });
        var token = "dz_api_" + Guid.NewGuid().ToString("N");
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = ApiTokenService.Hash(token),
            Prefix = ApiTokenService.DisplayPrefix(token), Scope = scope, ExpiresAt = expiresAt,
        });
        db.SaveChanges();
        return (db, userId, token);
    }

    [Fact]
    public async Task Valid_token_returns_user_and_scope()
    {
        var (db, userId, token) = Seed(ApiTokenScope.ReadOnly, expiresAt: null);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        var result = await auth.AuthenticateAsync(token, default);
        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal(ApiTokenScope.ReadOnly, result.Scope);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Feature_disabled_rejects_even_valid_token()
    {
        var (db, _, token) = Seed(ApiTokenScope.ReadWrite, expiresAt: null, apiEnabled: false);
        var auth = new ApiTokenAuthenticator(db, new FixedPlatformSettings(db));
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }
}
```

> `FixedPlatformSettings` is a tiny `IPlatformSettingsService` fake returning the seeded singleton row. If `TestSupport` has no such fake, add one to `tests/Diariz.Api.TestSupport/Infrastructure/` returning `db.PlatformSettings.First()`. (Do not add a mocking library - hand-roll the fake, per the repo convention.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokenAuthenticatorTests"`
Expected: FAIL to compile (`AuthenticateAsync` returns `Guid?`, no `ApiTokenAuth`).

- [ ] **Step 3: Change the authenticator to return scope + enforce expiry**

Replace the interface + method in `src/Diariz.Api/Services/ApiTokenAuthenticator.cs`:

```csharp
/// <summary>A verified API token: the owning user and the token's capability.</summary>
public sealed record ApiTokenAuth(Guid UserId, ApiTokenScope Scope);

public interface IApiTokenAuthenticator
{
    /// <summary>Verifies a presented API token. Returns the owner + scope when the feature is enabled, the
    /// token matches, and it has not expired; else null.</summary>
    Task<ApiTokenAuth?> AuthenticateAsync(string? token, CancellationToken ct);
}
```

And the body (keep the LastUsedAt throttle, add the expiry check):

```csharp
    public async Task<ApiTokenAuth?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var settings = await _platform.GetAsync(ct);
        if (!settings.ApiAccessEnabled) return null;

        var hash = ApiTokenService.Hash(token.Trim());
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return null;

        if (row.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) return null;

        var now = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return new ApiTokenAuth(row.UserId, row.Scope);
    }
```

Add `using Diariz.Domain.Entities;` if not present.

- [ ] **Step 4: Emit the scope claim in the handler**

In `src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs`, add the claim-type constant near `SchemeName`:

```csharp
    /// <summary>Claim carrying the token's <see cref="ApiTokenScope"/> (value = enum name). Only ApiKey-authed
    /// principals carry it; the write-block middleware reads it.</summary>
    public const string ScopeClaimType = "diariz:api_scope";
```

Change the authenticate body to use the new result and add the claim:

```csharp
        var auth = await _authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (auth is null) return AuthenticateResult.Fail("Invalid API token or API access is disabled.");

        var user = await _users.FindByIdAsync(auth.UserId.ToString());
        if (user is null) return AuthenticateResult.Fail("Token owner no longer exists.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, auth.UserId.ToString()),
            new(ClaimTypes.Name, user.FullName ?? user.Email ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
            new(ScopeClaimType, auth.Scope.ToString()),
        };
```

Add `using Diariz.Domain.Entities;` for `ApiTokenScope` if the file needs it (the claim uses `auth.Scope.ToString()`, so only if referenced by name).

- [ ] **Step 5: Update any other caller of the old return shape**

Search: `git grep -n "AuthenticateAsync" src/Diariz.Api` - only the ApiKey handler consumes `IApiTokenAuthenticator`. If a unit test double implements `IApiTokenAuthenticator`, update it to return `ApiTokenAuth?`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokenAuthenticatorTests"`
Expected: PASS. Then `dotnet build Diariz.slnx` to confirm the handler still compiles.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/ApiTokenAuthenticator.cs src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs tests/Diariz.Api.Tests/ApiTokenAuthenticatorTests.cs tests/Diariz.Api.TestSupport
git commit -m "feat: enforce API-token expiry and surface scope as a claim"
```

---

## Task 3: Read-only write-block middleware

**Files:**
- Create: `src/Diariz.Api/Auth/ApiTokenScopeMiddleware.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokenScopePolicyTests.cs` (pure predicate) + `tests/Diariz.Api.IntegrationTests/ReadOnlyTokenTests.cs` (pipeline)

**Interfaces:**
- Produces: `static bool ApiTokenScopePolicy.BlocksWrite(string method, ApiTokenScope scope)`; `ApiTokenScopeMiddleware` returning 403 for a blocked request.
- Consumes: `ApiKeyAuthenticationHandler.ScopeClaimType` (Task 2), `ApiTokenScope` (Task 1).

- [ ] **Step 1: Write the failing unit test for the pure predicate**

Create `tests/Diariz.Api.Tests/ApiTokenScopePolicyTests.cs`:

```csharp
using Diariz.Api.Auth;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenScopePolicyTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ReadOnly_blocks_unsafe_verbs(string method) =>
        Assert.True(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadOnly));

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ReadOnly_allows_safe_verbs(string method) =>
        Assert.False(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadOnly));

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public void ReadWrite_allows_everything(string method) =>
        Assert.False(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadWrite));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokenScopePolicyTests"`
Expected: FAIL to compile (`ApiTokenScopePolicy` missing).

- [ ] **Step 3: Write the middleware + predicate**

`src/Diariz.Api/Auth/ApiTokenScopeMiddleware.cs`:

```csharp
using System.Security.Claims;
using Diariz.Domain.Entities;

namespace Diariz.Api.Auth;

/// <summary>Pure rule: a read-only token may not perform an unsafe (state-changing) HTTP verb.</summary>
public static class ApiTokenScopePolicy
{
    public static bool BlocksWrite(string method, ApiTokenScope scope) =>
        scope == ApiTokenScope.ReadOnly
        && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method));
}

/// <summary>Rejects unsafe verbs from a read-only <c>dz_api_</c> token with 403. JWT/browser sessions carry no
/// scope claim and are unaffected. Runs after authentication so <c>context.User</c> is populated.</summary>
public sealed class ApiTokenScopeMiddleware
{
    private readonly RequestDelegate _next;
    public ApiTokenScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var scopeClaim = context.User.FindFirstValue(ApiKeyAuthenticationHandler.ScopeClaimType);
        if (scopeClaim is not null
            && Enum.TryParse<ApiTokenScope>(scopeClaim, out var scope)
            && ApiTokenScopePolicy.BlocksWrite(context.Request.Method, scope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This API token is read-only." });
            return;
        }
        await _next(context);
    }
}
```

- [ ] **Step 4: Register the middleware after authentication**

In `src/Diariz.Api/Program.cs`, immediately after `app.UseAuthentication();` and before `app.UseAuthorization();`, add:

```csharp
app.UseMiddleware<Diariz.Api.Auth.ApiTokenScopeMiddleware>();
```

- [ ] **Step 5: Run the unit test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokenScopePolicyTests"`
Expected: PASS.

- [ ] **Step 6: Write the integration test proving the pipeline blocks writes**

Create `tests/Diariz.Api.IntegrationTests/ReadOnlyTokenTests.cs`. Mint a read-only token for a seeded user (via the containers' `HttpClient` factory the sibling integration tests use), then assert: `GET /api/recordings` returns 200, `POST` to any write endpoint returns 403 with the read-only message. Follow the existing integration test harness for booting the API host and creating a token row directly in the DB (set `Scope = ApiTokenScope.ReadOnly`, `PlatformSettings.ApiAccessEnabled = true`). Assert:

```csharp
Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/recordings", token))).StatusCode);
Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/api/user/api-tokens", token))).StatusCode);
```

> Use whatever request-helper / WebApplicationFactory pattern the other integration tests use; the assertion is the contract. If no HTTP-level integration harness exists yet, cover this with a focused middleware unit test that builds a `DefaultHttpContext` with a read-only scope claim and a `POST` method, invokes `ApiTokenScopeMiddleware`, and asserts `context.Response.StatusCode == 403` and that `next` was not called.

- [ ] **Step 7: Run it**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ReadOnlyTokenTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Auth/ApiTokenScopeMiddleware.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/ApiTokenScopePolicyTests.cs tests/Diariz.Api.IntegrationTests/ReadOnlyTokenTests.cs
git commit -m "feat: reject writes from read-only API tokens"
```

---

## Task 4: Create-token API accepts scope + expiry; list exposes them

**Files:**
- Modify: `src/Diariz.Api/Controllers/ApiTokensController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokensControllerTests.cs`

**Interfaces:**
- Produces: `CreateApiTokenRequest(string? Name, bool ReadOnly = false, DateTimeOffset? ExpiresAt = null)`; `ApiTokenDto(..., string Scope, DateTimeOffset? ExpiresAt)`.

- [ ] **Step 1: Write the failing test**

Create/extend `tests/Diariz.Api.Tests/ApiTokensControllerTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class ApiTokensControllerTests
{
    private static ApiTokensController ForUser(DiarizDbContext db, Guid userId)
    {
        var c = new ApiTokensController(db, new ApiTokenService());
        c.ControllerContext = Http.Context(userId);
        return c;
    }

    [Fact]
    public async Task Create_persists_readonly_scope_and_expiry()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        await db.SaveChangesAsync();

        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var c = ForUser(db, userId);
        var created = await c.Create(new CreateApiTokenRequest("ci", ReadOnly: true, ExpiresAt: expires));

        var row = await db.ApiAccessTokens.SingleAsync();
        Assert.Equal(ApiTokenScope.ReadOnly, row.Scope);
        Assert.Equal(expires, row.ExpiresAt);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task List_reports_scope_and_expiry()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "u@e.com", UserName = "u@e.com" });
        db.ApiAccessTokens.Add(new ApiAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = "h", Prefix = "dz_api_x",
            Scope = ApiTokenScope.ReadOnly, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var list = await ForUser(db, userId).List();
        Assert.Equal("ReadOnly", list[0].Scope);
        Assert.NotNull(list[0].ExpiresAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokensControllerTests"`
Expected: FAIL to compile (`CreateApiTokenRequest` has no `ReadOnly`/`ExpiresAt`; `ApiTokenDto` has no `Scope`).

- [ ] **Step 3: Extend the DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, replace the two records:

```csharp
public record ApiTokenDto(
    Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    string Scope, DateTimeOffset? ExpiresAt);

public record CreateApiTokenRequest(string? Name, bool ReadOnly = false, DateTimeOffset? ExpiresAt = null);
```

(`ApiTokenCreatedDto` is unchanged.)

- [ ] **Step 4: Set scope + expiry in Create, project them in List**

In `ApiTokensController.List`, extend the projection:

```csharp
            .Select(t => new ApiTokenDto(
                t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.Scope.ToString(), t.ExpiresAt))
```

In `ApiTokensController.Create`, set the new fields on the row (reject a past expiry):

```csharp
        if (req.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            return BadRequest("Expiry must be in the future.");

        var g = _tokens.Generate();
        var row = new ApiAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = name,
            TokenHash = g.Hash,
            Prefix = g.Prefix,
            Scope = req.ReadOnly ? ApiTokenScope.ReadOnly : ApiTokenScope.ReadWrite,
            ExpiresAt = req.ExpiresAt,
        };
```

Add `using Diariz.Domain.Entities;` if not already imported.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ApiTokensControllerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/ApiTokensController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/ApiTokensControllerTests.cs
git commit -m "feat: mint API tokens with a scope and optional expiry"
```

---

## Task 5: Token UI - read-only + expiry (web)

**Files:**
- Modify: `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/components/DeveloperAccessSection.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/DeveloperAccessSection.test.tsx`

**Interfaces:**
- Consumes: `POST /api/user/api-tokens` now accepts `{ name, readOnly, expiresAt }`.
- Produces: `CreateApiTokenOptions { readOnly: boolean; expiresAt: string | null }`; `api.createApiToken(name, options?)`.

- [ ] **Step 1: Write the failing test**

Extend `apps/web/src/components/DeveloperAccessSection.test.tsx` with a test that ticks a "read-only" checkbox and generates, asserting the API call carried `readOnly: true`. Mock `../lib/api` and assert on the mock:

```tsx
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
// ...existing setup (QueryClientProvider + MemoryRouter, vi.mock("../lib/api")) ...

it("passes read-only when the box is ticked", async () => {
  const createApiToken = vi.mocked(api.createApiToken).mockResolvedValue({
    id: "1", name: "t", prefix: "dz_api_x", token: "dz_api_secret",
  });
  render(<Wrapped />);
  fireEvent.click(await screen.findByLabelText(/read-only/i));
  fireEvent.click(screen.getByRole("button", { name: /generate/i }));
  await waitFor(() =>
    expect(createApiToken).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ readOnly: true })));
});
```

> Match the file's existing render helper (`Wrapped` / provider wrapper) and its `vi.mock("../lib/api")` block. Use the actual label text you add in Step 4.

- [ ] **Step 2: Run test to verify it fails**

Run (from `apps/web`): `npm test -- DeveloperAccessSection`
Expected: FAIL (no read-only control; `createApiToken` called with one arg).

- [ ] **Step 3: Extend the TS types + api client**

In `apps/web/src/lib/types.ts`, extend the token interfaces and add options:

```ts
export interface ApiToken {
  id: string; name: string; prefix: string; createdAt: string; lastUsedAt: string | null;
  scope: "ReadOnly" | "ReadWrite"; expiresAt: string | null;
}
export interface CreateApiTokenOptions { readOnly: boolean; expiresAt: string | null; }
```

In `apps/web/src/lib/api.ts`, change `createApiToken`:

```ts
async createApiToken(name: string, options?: CreateApiTokenOptions): Promise<ApiTokenCreated> {
  const { data } = await http.post<ApiTokenCreated>("/api/user/api-tokens", {
    name,
    readOnly: options?.readOnly ?? false,
    expiresAt: options?.expiresAt ?? null,
  });
  return data;
},
```

Import `CreateApiTokenOptions` where the types are imported.

- [ ] **Step 4: Add the read-only checkbox + optional expiry to the component**

In `apps/web/src/components/DeveloperAccessSection.tsx`, add local state and controls near the name input, and pass them to `generate()`:

```tsx
  const [readOnly, setReadOnly] = useState(false);
  const [expiresAt, setExpiresAt] = useState("");   // yyyy-mm-dd or empty
```

In `generate()`:

```tsx
      const tok = await api.createApiToken(name.trim() || t("apiDefaultName"), {
        readOnly,
        expiresAt: expiresAt ? new Date(expiresAt + "T23:59:59Z").toISOString() : null,
      });
```

Add the controls (label text drives the test's `getByLabelText(/read-only/i)`):

```tsx
      <label className="mt-2 flex items-center gap-2 text-xs">
        <input type="checkbox" checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} />
        {t("apiReadOnly")}
      </label>
      <label className="mt-1 flex items-center gap-2 text-xs">
        {t("apiExpiresOn")}
        <input type="date" value={expiresAt} onChange={(e) => setExpiresAt(e.target.value)}
               className="rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-800" />
      </label>
```

- [ ] **Step 5: Add the i18n keys in all four languages**

Add to `apps/web/src/locales/en/account.json`:

```json
  "apiReadOnly": "Read-only (cannot change anything)",
  "apiExpiresOn": "Expires on (optional)",
```

Add translated equivalents to `de/es/fr/account.json` (plain hyphens, no em dashes). For example `de`: `"Nur Lesezugriff (kann nichts andern)"`, `"Lauft ab am (optional)"`; `es`: `"Solo lectura (no puede cambiar nada)"`, `"Caduca el (opcional)"`; `fr`: `"Lecture seule (ne peut rien modifier)"`, `"Expire le (facultatif)"`.

- [ ] **Step 6: Run tests to verify they pass**

Run (from `apps/web`): `npm test -- DeveloperAccessSection` then `npm run build` (tsc typecheck).
Expected: PASS + clean typecheck.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts apps/web/src/components/DeveloperAccessSection.tsx apps/web/src/components/DeveloperAccessSection.test.tsx apps/web/src/locales
git commit -m "feat: choose read-only and expiry when minting an API token"
```

---

## Task 6: PlatformSettings gains McpAccessEnabled + WebhooksEnabled

**Files:**
- Modify: `src/Diariz.Domain/Entities/PlatformSettings.cs`
- Create: `src/Diariz.Domain/Migrations/<ts>_AddPlatformIntegrationToggles.cs` (generated, edited)
- Modify: `src/Diariz.Api/Controllers/PlatformSettingsController.cs`, `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs`

**Interfaces:**
- Produces: `PlatformSettings.McpAccessEnabled` (default true), `PlatformSettings.WebhooksEnabled` (default false); `PlatformSettingsDto` + `UpdatePlatformSettingsRequest` gain both fields.

- [ ] **Step 1: Write the failing test**

Extend `tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs` with a round-trip through `Update`:

```csharp
[Fact]
public async Task Update_persists_mcp_and_webhooks_toggles()
{
    var db = TestDb.Create();
    var c = AdminController(db);   // follow the file's existing helper that builds the controller with a ManagePlatform user
    var req = ValidUpdateRequest() with { McpAccessEnabled = false, WebhooksEnabled = true };
    var result = await c.Update(req);

    // Update returns `ToDto(s)`, which converts to ActionResult<PlatformSettingsDto> with .Value set.
    Assert.NotNull(result.Value);
    Assert.False(result.Value!.McpAccessEnabled);
    Assert.True(result.Value.WebhooksEnabled);
}
```

> Use the file's established helpers (`AdminController`, a valid-request factory). If none exists, build `UpdatePlatformSettingsRequest` with the required quota/timeout fields set to valid values and construct the controller with `Http.Context(adminUserId)` plus whatever permission seeding the sibling tests use.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PlatformSettingsControllerTests"`
Expected: FAIL to compile (`McpAccessEnabled`/`WebhooksEnabled` unknown on the request/DTO).

- [ ] **Step 3: Add the entity fields**

In `src/Diariz.Domain/Entities/PlatformSettings.cs`, after `ApiAccessEnabled`:

```csharp
    /// <summary>Master switch for the MCP server + dz_mcp_ tokens. On by default (bounded by env Mcp:Enabled).
    /// Seeded true in the migration so shipping this never disables an existing connector.</summary>
    public bool McpAccessEnabled { get; set; } = true;

    /// <summary>Master switch for outbound webhooks / user Automations. Off by default; used from Phase 2.</summary>
    public bool WebhooksEnabled { get; set; }
```

- [ ] **Step 4: Extend both DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, extend the platform-settings records (append the two fields; keep parameter order stable by appending):

```csharp
public record PlatformSettingsDto(
    long StarterQuotaBytes, long MaxQuotaBytes, MinutesGenerationMode MinutesGenerationMode,
    bool AutoDeleteAudioEnabled, int AudioRetentionDays, TimeOnly AudioDeletionTimeOfDay,
    bool ApiAccessEnabled, int LlmTimeoutSeconds,
    bool McpAccessEnabled, bool WebhooksEnabled);

public record UpdatePlatformSettingsRequest(
    long StarterQuotaBytes, long MaxQuotaBytes,
    MinutesGenerationMode MinutesGenerationMode = MinutesGenerationMode.SingleCall,
    bool AutoDeleteAudioEnabled = false,
    int AudioRetentionDays = PlatformSettings.DefaultAudioRetentionDays,
    TimeOnly AudioDeletionTimeOfDay = default,
    bool ApiAccessEnabled = false,
    int LlmTimeoutSeconds = PlatformSettings.DefaultLlmTimeoutSeconds,
    bool McpAccessEnabled = true,
    bool WebhooksEnabled = false);
```

- [ ] **Step 5: Wire them through the controller**

In `PlatformSettingsController.Update`, after `s.ApiAccessEnabled = req.ApiAccessEnabled;`:

```csharp
        s.McpAccessEnabled = req.McpAccessEnabled;
        s.WebhooksEnabled = req.WebhooksEnabled;
```

In `ToDto`, append the two fields:

```csharp
    private static PlatformSettingsDto ToDto(PlatformSettings s) => new(
        s.StarterQuotaBytes, s.MaxQuotaBytes, s.MinutesGenerationMode,
        s.AutoDeleteAudioEnabled, s.AudioRetentionDays, s.AudioDeletionTimeOfDay, s.ApiAccessEnabled,
        s.LlmTimeoutSeconds, s.McpAccessEnabled, s.WebhooksEnabled);
```

- [ ] **Step 6: Generate + fix the migration**

Run: `dotnet ef migrations add AddPlatformIntegrationToggles --project src/Diariz.Domain --startup-project src/Diariz.Api`

Open the generated file. Ensure `McpAccessEnabled` is added with `defaultValue: true` (so the existing singleton row stays MCP-enabled) and `WebhooksEnabled` with `defaultValue: false`:

```csharp
migrationBuilder.AddColumn<bool>(
    name: "McpAccessEnabled", table: "PlatformSettings", type: "boolean", nullable: false, defaultValue: true);
migrationBuilder.AddColumn<bool>(
    name: "WebhooksEnabled", table: "PlatformSettings", type: "boolean", nullable: false, defaultValue: false);
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PlatformSettingsControllerTests"`
Expected: PASS. Then `dotnet build Diariz.slnx`.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/PlatformSettings.cs src/Diariz.Domain/Migrations src/Diariz.Api/Controllers/PlatformSettingsController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs
git commit -m "feat: add MCP and Webhooks platform toggles"
```

---

## Task 7: Enforce McpAccessEnabled in the MCP auth path

**Files:**
- Modify: `src/Diariz.Api/Auth/McpBearerAuthenticationHandler.cs`
- Test: `tests/Diariz.Api.IntegrationTests/McpToggleTests.cs`

**Interfaces:**
- Consumes: `IPlatformSettingsService.GetAsync`, `PlatformSettings.McpAccessEnabled` (Task 6).

**Why the handler, not `MapMcp`:** the `MapMcp(...)` gate at `Program.cs:497` is a startup-time `if (mcpOptions.Enabled)` (the env kill-switch). `McpAccessEnabled` is a runtime DB flag, so it must be checked per-request. Gating in `HandleAuthenticateAsync` covers **both** the static-`dz_mcp_` and the OAuth token paths in one place.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/McpToggleTests.cs`: seed a valid `dz_mcp_` token, set `PlatformSettings.McpAccessEnabled = false`, and assert an authenticated `/mcp` POST returns 401; then set it true and assert it is accepted (not 401). Use the integration host + a real `dz_mcp_` token row (mirror an existing MCP integration test if present).

```csharp
[Fact]
public async Task Mcp_requests_are_401_when_platform_toggle_is_off()
{
    // arrange: McpAccessEnabled = false in PlatformSettings; a valid dz_mcp_ token
    var res = await client.SendAsync(McpInitialize(token));
    Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
}
```

> If there is no existing `/mcp` HTTP integration test to copy the request shape from, instead unit-test the handler by constructing it with an `IPlatformSettingsService` fake returning `McpAccessEnabled = false` and asserting `HandleAuthenticateAsync` yields `AuthenticateResult.Failure` for a valid `dz_mcp_` token. Keep whichever harness the repo already supports for auth handlers.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~McpToggleTests"`
Expected: FAIL (MCP still authenticates with the toggle off).

- [ ] **Step 3: Inject the platform service and gate at the top of the handler**

In `src/Diariz.Api/Auth/McpBearerAuthenticationHandler.cs`, add `IPlatformSettingsService` to the constructor (store as `_platform`), then at the very start of `HandleAuthenticateAsync`, after confirming a Bearer header is present:

```csharp
        var token = header[Prefix.Length..].Trim();

        // Runtime platform kill-switch: no MCP credential authenticates while the feature is off.
        var settings = await _platform.GetAsync(Context.RequestAborted);
        if (!settings.McpAccessEnabled) return AuthenticateResult.Fail("MCP access is disabled.");
```

Add `using Diariz.Api.Services;` if needed.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~McpToggleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Auth/McpBearerAuthenticationHandler.cs tests/Diariz.Api.IntegrationTests/McpToggleTests.cs
git commit -m "feat: gate MCP auth on the McpAccessEnabled platform toggle"
```

---

## Task 8: Platform-toggle UI (SettingsModal)

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/components/SettingsModal.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/SettingsModal.test.tsx`

**Interfaces:**
- Consumes: `PlatformSettings` now has `mcpAccessEnabled`, `webhooksEnabled`.

- [ ] **Step 1: Write the failing test**

Extend `apps/web/src/components/SettingsModal.test.tsx`: with the Integration tab shown, assert the MCP and Webhooks checkboxes render and that saving PUTs `mcpAccessEnabled`/`webhooksEnabled`. Follow the file's existing mock of `../lib/api` (which returns a `platform-settings` object) and its tab-selection helper:

```tsx
it("saves the MCP and Webhooks toggles", async () => {
  const update = vi.mocked(api.updatePlatformSettings).mockResolvedValue(basePlatform);
  renderIntegrationTab();                       // existing helper that opens the Integration tab
  fireEvent.click(screen.getByLabelText(/webhooks/i));
  fireEvent.click(screen.getByRole("button", { name: /ok|save/i }));
  await waitFor(() =>
    expect(update).toHaveBeenCalledWith(expect.objectContaining({ webhooksEnabled: true })));
});
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `apps/web`): `npm test -- SettingsModal`
Expected: FAIL (no such checkboxes / fields).

- [ ] **Step 3: Extend the TS type**

In `apps/web/src/lib/types.ts`, add to `PlatformSettings`:

```ts
  mcpAccessEnabled: boolean;
  webhooksEnabled: boolean;
```

- [ ] **Step 4: Wire state, load, save, and UI in SettingsModal**

Add state next to `apiAccessEnabled`:

```tsx
const [mcpAccessEnabled, setMcpAccessEnabled] = useState(true);
const [webhooksEnabled, setWebhooksEnabled] = useState(false);
```

In the `useEffect` that loads `platform`, after `setApiAccessEnabled(...)`:

```tsx
setMcpAccessEnabled(platform.mcpAccessEnabled);
setWebhooksEnabled(platform.webhooksEnabled);
```

In `onOk`'s `updatePlatformSettings({...})` body, after `apiAccessEnabled,`:

```tsx
      mcpAccessEnabled,
      webhooksEnabled,
```

In the Integration tab JSX, add two checkbox blocks mirroring the API one:

```tsx
<label className="flex items-center gap-2 text-sm">
  <input type="checkbox" checked={mcpAccessEnabled}
         onChange={(e) => setMcpAccessEnabled(e.target.checked)} />
  <span className="font-medium text-gray-700 dark:text-gray-200">{t("mcpAccessEnabledLabel")}</span>
</label>
<p className="text-xs text-gray-400 dark:text-gray-500">{t("mcpAccessEnabledHelp")}</p>

<label className="flex items-center gap-2 text-sm">
  <input type="checkbox" checked={webhooksEnabled}
         onChange={(e) => setWebhooksEnabled(e.target.checked)} />
  <span className="font-medium text-gray-700 dark:text-gray-200">{t("webhooksEnabledLabel")}</span>
</label>
<p className="text-xs text-gray-400 dark:text-gray-500">{t("webhooksEnabledHelp")}</p>
```

- [ ] **Step 5: Add i18n keys in all four languages**

Add to `en/account.json` (translate for de/es/fr, plain hyphens):

```json
  "mcpAccessEnabledLabel": "Claude / MCP access",
  "mcpAccessEnabledHelp": "Let users connect Claude to their meetings and mint MCP tokens.",
  "webhooksEnabledLabel": "Automations (webhooks)",
  "webhooksEnabledHelp": "Let users send their meeting events to tools like Zapier and n8n.",
```

- [ ] **Step 6: Run tests to verify they pass**

Run (from `apps/web`): `npm test -- SettingsModal` then `npm run build`.
Expected: PASS + clean typecheck.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/SettingsModal.tsx apps/web/src/components/SettingsModal.test.tsx apps/web/src/locales
git commit -m "feat: admin toggles for MCP and Webhooks in Settings"
```

---

## Task 9: API-reference links open in a new tab

**Files:**
- Modify: `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/components/DeveloperAccessSection.tsx`
- Test: `apps/web/src/components/SettingsModal.test.tsx`, `apps/web/src/components/DeveloperAccessSection.test.tsx`

**Context:** both links point to `/developers/api`. The Settings one works but opens in the same tab; the Preferences (`DeveloperAccessSection`) one navigates in-app from inside the modal and lands on a blank page. Opening both in a new tab (full SPA load at the route) fixes both. Keep the `href`/`to` value `/developers/api` (existing tests assert it).

- [ ] **Step 1: Update the failing tests first**

In `SettingsModal.test.tsx`, extend the existing link assertion (line ~141):

```tsx
const link = screen.getByRole("link", { name: /view api reference/i });
expect(link.getAttribute("href")).toBe("/developers/api");
expect(link.getAttribute("target")).toBe("_blank");
```

In `DeveloperAccessSection.test.tsx`, extend the existing link test (line ~34) with the same `target` assertion.

- [ ] **Step 2: Run tests to verify they fail**

Run (from `apps/web`): `npm test -- SettingsModal DeveloperAccessSection`
Expected: FAIL (`target` is null).

- [ ] **Step 3: Open the Settings link in a new tab**

In `SettingsModal.tsx`, change the reference `Link` to a new-tab anchor (drop the in-app `onClick={onClose}` navigation):

```tsx
<a
  href="/developers/api"
  target="_blank"
  rel="noopener noreferrer"
  className="inline-block text-xs text-indigo-600 hover:underline dark:text-indigo-400"
>
  {t("apiViewReference")} →
</a>
```

- [ ] **Step 4: Open the Preferences link in a new tab**

In `DeveloperAccessSection.tsx`, change the `Link` to:

```tsx
<a href="/developers/api" target="_blank" rel="noopener noreferrer" className={btn}>
  {t("apiViewReference")}
</a>
```

Remove the now-unused `import { Link } from "react-router-dom";` from each file if it is no longer referenced.

- [ ] **Step 5: Run tests to verify they pass**

Run (from `apps/web`): `npm test -- SettingsModal DeveloperAccessSection` then `npm run build`.
Expected: PASS + clean typecheck.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/SettingsModal.tsx apps/web/src/components/DeveloperAccessSection.tsx apps/web/src/components/SettingsModal.test.tsx apps/web/src/components/DeveloperAccessSection.test.tsx
git commit -m "fix: open the API reference in a new tab from both entry points"
```

---

## Task 10: Release, docs, and verification

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`
- Test: `apps/web/src/lib/releases.test.ts` (existing assertion), full suites

- [ ] **Step 1: Bump the version everywhere to `0.151.0`**

Set `version.json` to `{ "version": "0.151.0" }`; set the `version` field in `apps/web/package.json`, `apps/desktop/package.json`, and `<Version>` in `src/Diariz.Api/Diariz.Api.csproj` to `0.151.0`.

- [ ] **Step 2: Prepend the release entry**

Add a new `RELEASES[0]` to `apps/web/src/lib/releases.ts` (version must equal `version.json`):

```ts
  {
    version: "0.151.0",
    date: "2026-07-24",
    pr: 0, // set to the PR number when opened
    headline: "Granular integration controls and safer API tokens",
    summary:
      "Platform admins can now enable API access, Claude/MCP, and Automations (webhooks) independently. " +
      "Personal API tokens can be minted read-only and given an expiry date, so a token pasted into an " +
      "external tool can be least-privilege and time-boxed. The in-app API reference now opens in a new tab.",
    added: [
      "Read-only scope and an optional expiry date when creating a personal API token",
      "Separate platform toggles for API access, Claude/MCP, and Automations (webhooks)",
    ],
    fixed: [
      "The API reference link in Preferences no longer opens a blank page; both API reference links open in a new tab",
    ],
  },
```

- [ ] **Step 3: Update the About-box CAPABILITIES rows**

In `releases.ts`, update the **API access** row to mention scope/expiry and adjust the **Connect Claude (MCP)** / add an **Automations** mention consistent with the admin toggles (concise table cells, no em dashes). Keep it a single line per feature.

- [ ] **Step 4: Update README + features + architecture + schema docs**

- `README.md` Features table: adjust the API-access row (scoped/expiring tokens) and note the three integration toggles.
- `docs/features.md`: matching prose bullets (lockstep with the README row).
- `docs/Overall_Synopsis_of_Platform.md`: note the three platform toggles (API/MCP/Webhooks) and token scope/expiry; note that the formula run endpoint (`POST /api/recordings/{id}/formulas/{formulaId}/run`, 202 + result id) and result-text GET are the inbound fire/fetch surface for automation.
- `docs/Data_Schema.md`: add the `ApiAccessTokens.Scope` + `ExpiresAt` columns, the `PlatformSettings.McpAccessEnabled` + `WebhooksEnabled` columns, and both migrations in the migration-history table.

- [ ] **Step 5: Verify the OpenAPI already exposes the formula fire/fetch endpoints** (no new code expected)

The curated document at `GET /api/openapi/v1.json` includes user-facing `api/*` endpoints (excluding `api/oauth`). Confirm `POST /api/recordings/{recordingId}/formulas/{formulaId}/run` and `GET /api/recordings/{recordingId}/formula-results/{id}` appear. If a curation filter drops them, add them to the include set; otherwise no change. (This satisfies the spec's inbound "fire a formula / fetch its output" requirement without new endpoints.)

- [ ] **Step 6: Run the full suites**

Run:
- `dotnet build Diariz.slnx` (catches integration/CodeQL compile breaks)
- `dotnet test tests/Diariz.Api.Tests`
- `dotnet test tests/Diariz.Api.IntegrationTests` (Docker)
- from `apps/web`: `npm test` and `npm run build`

Expected: all green; `releases.test.ts` passes (RELEASES[0].version === version.json).

- [ ] **Step 7: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts README.md docs
git commit -m "chore: release 0.151.0 - integration toggles and scoped API tokens"
```

- [ ] **Step 8: Push and open the PR**

```bash
git push -u origin feat/integrations-webhooks
gh pr create --title "Integrations Phase 1: platform toggles + scoped API tokens" --body "..."
```

PR body must state: **deployment surface = server redeploy only (no desktop release)**; migrations additive + forward-restore-safe (no `CurrentFormat` bump); this is Phase 1 of 3 (webhooks core + Workflow Signals follow). Then set the real PR number into `RELEASES[0].pr` and amend/commit.

---

## Notes for the executor

- **Spec deviation (intentional):** the spec (section 11.2) proposed *adding* `POST /api/recordings/{id}/formula-runs` and `GET /api/formula-results/{id}`. Those capabilities **already exist** as `POST .../formulas/{formulaId}/run` (async, 202 + result id) and `GET .../formula-results/{id}` (result text). Building new duplicates would violate DRY/YAGNI, so Task 10 Step 5 **verifies + documents** the existing endpoints instead. If a reviewer flags the missing literal routes, this note is the rationale.
- **Return-shape change (Task 2)** touches every `IApiTokenAuthenticator` consumer - there is only one (the ApiKey handler) plus possibly a test double. Grep before assuming.
- **Migration defaults are correctness-critical** (Tasks 1 & 6): `Scope` default `1`/ReadWrite and `McpAccessEnabled` default `true` protect existing tokens and the live MCP connector. Do not skip the manual edit of the generated migrations.
