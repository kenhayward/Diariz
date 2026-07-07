# User API Access - PR A (Credential + Platform Gate) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give platform users a personal API key (`dz_api_…`) that authenticates against the existing REST API with full session parity, gated behind a Platform-Admin enable switch that defaults OFF.

**Architecture:** Mirror the existing MCP personal-access-token stack (entity → service → authenticator → auth scheme → controller) as a *separate* `dz_api_` credential. Make it satisfy every `[Authorize]` variant by switching the default authenticate scheme to a **forwarding policy scheme** that routes `Bearer dz_api_…` to a new `ApiKey` handler and everything else to JWT. A platform switch (`PlatformSettings.ApiAccessEnabled`, default false) gates authentication; the flag is surfaced to users so the UI lights up only when enabled.

**Tech Stack:** ASP.NET Core 10, EF Core (Npgsql), xUnit (+ Testcontainers integration), React 19 + TS + Vitest.

**Deviation from spec (approved mechanism refinement):** The spec proposed adding both schemes to the default *authorization policy*. That does not cover `[Authorize(Roles=…)]` endpoints (e.g. `PlatformSettingsController.Update`), which authenticate with the default *authenticate* scheme. This plan uses a **forwarding policy scheme** as the default authenticate scheme instead — the standard idiom for "accept credential A or B everywhere". Behaviour for existing flows is unchanged (no `Authorization` header → forwards to JWT, so SignalR/audio/backup query-string auth is untouched; `/mcp` names its own scheme and is unaffected).

**Reference files to mirror (read these first):**
- `src/Diariz.Domain/Entities/McpAccessToken.cs`
- `src/Diariz.Api/Services/McpTokenService.cs`, `McpTokenAuthenticator.cs`
- `src/Diariz.Api/Auth/McpBearerAuthenticationHandler.cs`
- `src/Diariz.Api/Controllers/McpTokensController.cs`
- `apps/web/src/components/McpAccessSection.tsx`

---

## File map

**Create**
- `src/Diariz.Domain/Entities/ApiAccessToken.cs`
- `src/Diariz.Api/Services/ApiTokenService.cs`
- `src/Diariz.Api/Services/ApiTokenAuthenticator.cs`
- `src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs`
- `src/Diariz.Api/Controllers/ApiTokensController.cs`
- `apps/web/src/components/DeveloperAccessSection.tsx`
- Tests: `ApiTokenServiceTests.cs`, `ApiTokenAuthenticatorTests.cs`, `ApiTokensControllerTests.cs`, `tests/Diariz.Api.IntegrationTests/ApiAccessIntegrationTests.cs`, `apps/web/src/components/DeveloperAccessSection.test.tsx`
- Migration: `AddApiAccessTokens` (adds `ApiAccessTokens` table + `PlatformSettings.ApiAccessEnabled` column)

**Modify**
- `src/Diariz.Domain/Entities/PlatformSettings.cs`, `src/Diariz.Domain/DiarizDbContext.cs`
- `src/Diariz.Api/Program.cs` (DI + auth schemes + forwarding default)
- `src/Diariz.Api/Controllers/PlatformSettingsController.cs`, `UserProfileController.cs`
- `src/Diariz.Api/Contracts/ApiDtos.cs` (new token DTOs + `ApiAccessEnabled` on `PlatformSettingsDto`/`UpdatePlatformSettingsRequest`/`UserProfileDto`)
- `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- `apps/web/src/components/PreferencesModal.tsx`, `apps/web/src/components/SettingsModal.tsx`
- `apps/web/src/locales/{en,es,fr,de}/account.json`
- Docs: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/lib/releases.ts`
- Version mirrors: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`

---

## Task 1: Domain — `ApiAccessToken` entity, platform flag, DbContext, migration

**Files:**
- Create: `src/Diariz.Domain/Entities/ApiAccessToken.cs`
- Modify: `src/Diariz.Domain/Entities/PlatformSettings.cs`, `src/Diariz.Domain/DiarizDbContext.cs`
- Migration: `src/Diariz.Domain/Migrations/*_AddApiAccessTokens.cs`

- [ ] **Step 1: Create the entity** (`ApiAccessToken.cs`) — a verbatim copy of `McpAccessToken.cs` with the class renamed to `ApiAccessToken` and the XML doc updated to "REST API" instead of "MCP client / `/mcp`":

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A personal access token that lets a user call the Diariz REST API as themselves. Only the
/// SHA-256 hash of the secret is stored (never the plaintext, which is shown once at generation). A user may
/// hold several named tokens; revoking one deletes the row.</summary>
public class ApiAccessToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}
```

- [ ] **Step 2: Add the platform flag** — in `PlatformSettings.cs`, add after `AudioDeletionTimeOfDay`:

```csharp
    /// <summary>Master switch for user API access (personal `dz_api_` tokens). Off by default: no API key
    /// authenticates until the Platform Administrator opts in.</summary>
    public bool ApiAccessEnabled { get; set; }
```

- [ ] **Step 3: Register in `DiarizDbContext.cs`** — add the DbSet next to `McpAccessTokens` (line ~28):

```csharp
    public DbSet<ApiAccessToken> ApiAccessTokens => Set<ApiAccessToken>();
```

and add an entity config block immediately after the `McpAccessToken` one (line ~343), identical shape:

```csharp
        builder.Entity<ApiAccessToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.Property(t => t.Name).HasMaxLength(128);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Prefix).HasMaxLength(32);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Create the migration**

Run: `dotnet ef migrations add AddApiAccessTokens --project src/Diariz.Domain --startup-project src/Diariz.Api`
Expected: creates `ApiAccessTokens` (with unique `TokenHash` index + `UserId` index + cascade FK) and adds a `bool ApiAccessEnabled` column (default `false`) to `PlatformSettings`.

- [ ] **Step 5: Inspect the generated migration** — confirm the `AddColumn<bool>("ApiAccessEnabled", ... defaultValue: false)` and no stray empty `UpdateData` for the seeded `PlatformSettings` row. (An empty `UpdateData` emits invalid `SET  WHERE` SQL — delete it if present; the `AddColumn` default backfills. This bit us in the Meeting-Types arc.)

- [ ] **Step 6: Build**

Run: `dotnet build Diariz.slnx`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain
git commit -m "feat(api-access): ApiAccessToken entity + PlatformSettings.ApiAccessEnabled + migration"
```

---

## Task 2: `ApiTokenService` (generate + hash)

**Files:**
- Create: `src/Diariz.Api/Services/ApiTokenService.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokenServiceTests.cs`

- [ ] **Step 1: Write the failing test** (`ApiTokenServiceTests.cs`):

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ApiTokenServiceTests
{
    [Fact]
    public void Generate_ProducesPrefixedToken_WithMatchingHashAndDisplayPrefix()
    {
        var g = new ApiTokenService().Generate();
        Assert.StartsWith("dz_api_", g.Token);
        Assert.Equal(ApiTokenService.Hash(g.Token), g.Hash);
        Assert.Equal(64, g.Hash.Length);                 // lowercase hex SHA-256
        Assert.Equal(g.Token[..13], g.Prefix);           // dz_api_ + 6 chars
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        Assert.NotEqual(new ApiTokenService().Generate().Token, new ApiTokenService().Generate().Token);
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokenServiceTests`
Expected: FAIL (compile error — `ApiTokenService` does not exist).

- [ ] **Step 3: Implement** (`ApiTokenService.cs`) — a copy of `McpTokenService.cs` with `Mcp`→`Api` in type names and the prefix changed:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Services;

/// <summary>A freshly minted API token: plaintext <paramref name="Token"/> (shown once), its
/// <paramref name="Hash"/> (persisted), and a short non-secret <paramref name="Prefix"/> for display.</summary>
public sealed record GeneratedApiToken(string Token, string Hash, string Prefix);

public interface IApiTokenService
{
    GeneratedApiToken Generate();
}

/// <summary>Generates and hashes personal REST-API tokens: <c>dz_api_</c> + base64url(32 random bytes),
/// stored only as a lowercase-hex SHA-256 hash (verified by hashing an incoming token and looking it up).</summary>
public sealed class ApiTokenService : IApiTokenService
{
    public const string TokenPrefix = "dz_api_";
    public const int DisplayPrefixLength = 13;

    public GeneratedApiToken Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = TokenPrefix + Base64UrlEncode(bytes);
        return new GeneratedApiToken(token, Hash(token), DisplayPrefix(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    public static string DisplayPrefix(string token) =>
        string.IsNullOrEmpty(token) || token.Length <= DisplayPrefixLength ? token ?? "" : token[..DisplayPrefixLength];

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokenServiceTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/ApiTokenService.cs tests/Diariz.Api.Tests/ApiTokenServiceTests.cs
git commit -m "feat(api-access): ApiTokenService (dz_api_ generate + hash)"
```

---

## Task 3: `ApiTokenAuthenticator` (hash lookup + platform-enabled gate)

The authenticator differs from the MCP one in two ways: it is gated on `PlatformSettings.ApiAccessEnabled` (returns null when the feature is off), and it returns the row's `UserId` for the handler to build a full-parity principal.

**Files:**
- Create: `src/Diariz.Api/Services/ApiTokenAuthenticator.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokenAuthenticatorTests.cs`

- [ ] **Step 1: Write the failing test.** Uses `TestDb.Create()` and the real `PlatformSettingsService`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenAuthenticatorTests
{
    private static async Task<(ApiTokenAuthenticator auth, Diariz.Domain.DiarizDbContext db)> BuildAsync(bool enabled)
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, ApiAccessEnabled = enabled });
        await db.SaveChangesAsync();
        return (new ApiTokenAuthenticator(db, new PlatformSettingsService(db)), db);
    }

    private static async Task<string> SeedTokenAsync(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var g = new ApiTokenService().Generate();
        db.ApiAccessTokens.Add(new ApiAccessToken { Id = Guid.NewGuid(), UserId = userId, Name = "t", TokenHash = g.Hash, Prefix = g.Prefix });
        await db.SaveChangesAsync();
        return g.Token;
    }

    [Fact]
    public async Task Authenticate_ReturnsOwner_WhenEnabledAndTokenValid()
    {
        var (auth, db) = await BuildAsync(enabled: true);
        var user = Guid.NewGuid();
        var token = await SeedTokenAsync(db, user);
        Assert.Equal(user, await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_ReturnsNull_WhenFeatureDisabled()
    {
        var (auth, db) = await BuildAsync(enabled: false);
        var token = await SeedTokenAsync(db, Guid.NewGuid());
        Assert.Null(await auth.AuthenticateAsync(token, default));
    }

    [Fact]
    public async Task Authenticate_ReturnsNull_ForUnknownOrBlankToken()
    {
        var (auth, _) = await BuildAsync(enabled: true);
        Assert.Null(await auth.AuthenticateAsync("dz_api_nope", default));
        Assert.Null(await auth.AuthenticateAsync("", default));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokenAuthenticatorTests`
Expected: FAIL (compile — `ApiTokenAuthenticator` missing).

- [ ] **Step 3: Implement** (`ApiTokenAuthenticator.cs`):

```csharp
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IApiTokenAuthenticator
{
    /// <summary>Verifies a presented API token. Returns the owning user's id when the feature is enabled and
    /// the token matches (recording LastUsedAt), else null (blank/unknown token, or feature disabled).</summary>
    Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct);
}

public sealed class ApiTokenAuthenticator : IApiTokenAuthenticator
{
    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;

    public ApiTokenAuthenticator(DiarizDbContext db, IPlatformSettingsService platform)
    {
        _db = db;
        _platform = platform;
    }

    public async Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Platform kill-switch: no key authenticates while the feature is off.
        var settings = await _platform.GetAsync(ct);
        if (!settings.ApiAccessEnabled) return null;

        var hash = ApiTokenService.Hash(token.Trim());
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return row.UserId;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokenAuthenticatorTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/ApiTokenAuthenticator.cs tests/Diariz.Api.Tests/ApiTokenAuthenticatorTests.cs
git commit -m "feat(api-access): ApiTokenAuthenticator with platform enable gate"
```

---

## Task 4: `ApiKey` authentication handler (full-parity principal)

The handler recognises a `dz_api_` bearer, resolves the user id via the authenticator, then builds a principal carrying the user's `NameIdentifier` **and role claims** (so an admin's key satisfies `[Authorize("Admin")]` / `[Authorize(Roles=…)]`). Roles are loaded via `UserManager<ApplicationUser>`.

**Files:**
- Create: `src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs`
- (Handler behaviour is covered by the integration auth-matrix in Task 9 — no separate unit test, matching how `McpBearerAuthenticationHandler` is tested.)

- [ ] **Step 1: Implement** (`ApiKeyAuthenticationHandler.cs`):

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Auth;

public sealed class ApiKeyAuthSchemeOptions : AuthenticationSchemeOptions;

/// <summary>Authenticates a request bearing a personal REST-API token (<c>dz_api_…</c>) as the owning user,
/// with full session parity: the principal carries the user's id, name/email, and role claims, so ownership
/// checks and admin authorization work exactly as they do for a JWT session. Only invoked for `dz_api_`
/// bearers (routed here by the forwarding default scheme in Program.cs).</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string Prefix = "Bearer ";

    private readonly IApiTokenAuthenticator _authenticator;
    private readonly UserManager<ApplicationUser> _users;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IApiTokenAuthenticator authenticator, UserManager<ApplicationUser> users)
        : base(options, logger, encoder)
    {
        _authenticator = authenticator;
        _users = users;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[Prefix.Length..].Trim();
        if (!token.StartsWith(ApiTokenService.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult(); // not our credential; let the challenge 401

        var userId = await _authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (userId is null) return AuthenticateResult.Fail("Invalid API token or API access is disabled.");

        var user = await _users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return AuthenticateResult.Fail("Token owner no longer exists.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            new(ClaimTypes.Name, user.FullName ?? user.Email ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
        };
        foreach (var role in await _users.GetRolesAsync(user))
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Diariz.Api`
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/Diariz.Api/Auth/ApiKeyAuthenticationHandler.cs
git commit -m "feat(api-access): ApiKey auth handler (full-parity principal)"
```

---

## Task 5: Wire DI + schemes + forwarding default in `Program.cs`

**Files:** Modify `src/Diariz.Api/Program.cs`

- [ ] **Step 1: Register services** — near the other service registrations (e.g. beside where `IMcpTokenService`/`IMcpTokenAuthenticator` are added; search `McpTokenService`):

```csharp
builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
builder.Services.AddScoped<IApiTokenAuthenticator, ApiTokenAuthenticator>();
```

- [ ] **Step 2: Change the default authenticate scheme to a forwarding policy scheme.** Replace the opening of the auth registration (currently `builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`) with a policy scheme that forwards by token prefix, then keep the existing `.AddJwtBearer(...)` and `.AddScheme<McpAuthSchemeOptions, McpBearerAuthenticationHandler>(...)` chained after it, and add the ApiKey scheme:

```csharp
const string smartScheme = "smart";
builder.Services.AddAuthentication(smartScheme)
    .AddPolicyScheme(smartScheme, smartScheme, o =>
    {
        // Route personal API tokens to the ApiKey handler; everything else (JWT bearer, or no header for the
        // SignalR/audio/backup query-string flows) to JWT. Named schemes (e.g. the Mcp policy on /mcp) are
        // unaffected because they select their own scheme explicitly.
        o.ForwardDefaultSelector = ctx =>
        {
            string auth = ctx.Request.Headers.Authorization!;
            return !string.IsNullOrEmpty(auth)
                   && auth.StartsWith("Bearer " + Diariz.Api.Services.ApiTokenService.TokenPrefix, StringComparison.Ordinal)
                ? Diariz.Api.Auth.ApiKeyAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(o => { /* ...existing JwtBearer config unchanged... */ })
    .AddScheme<Diariz.Api.Auth.McpAuthSchemeOptions, Diariz.Api.Auth.McpBearerAuthenticationHandler>(
        Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<Diariz.Api.Auth.ApiKeyAuthSchemeOptions, Diariz.Api.Auth.ApiKeyAuthenticationHandler>(
        Diariz.Api.Auth.ApiKeyAuthenticationHandler.SchemeName, _ => { });
```

> Keep the existing `AddJwtBearer` body exactly as it is (the `TokenValidationParameters` + `OnMessageReceived` events). Only the outer `AddAuthentication(...)` default and the two appended `.AddScheme(...)` lines change.

- [ ] **Step 3: Build**

Run: `dotnet build src/Diariz.Api`
Expected: `Build succeeded`.

- [ ] **Step 4: Smoke-run the existing unit tests** (no regressions in auth-dependent tests)

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: all pass (existing count + the new Task 2/3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Program.cs
git commit -m "feat(api-access): forward Bearer dz_api_ to ApiKey scheme; register services"
```

---

## Task 6: `ApiTokensController` + DTOs

**Files:**
- Create: `src/Diariz.Api/Controllers/ApiTokensController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/ApiTokensControllerTests.cs`

- [ ] **Step 1: Add DTOs** — in `ApiDtos.cs`, next to the MCP token DTOs (line ~326):

```csharp
public record ApiTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
public record ApiTokenCreatedDto(Guid Id, string Name, string Prefix, string Token);
public record CreateApiTokenRequest(string? Name);
```

- [ ] **Step 2: Write the failing test** (`ApiTokensControllerTests.cs`) — mirror `McpTokensControllerTests` (create-returns-plaintext-once, list-hides-secret, revoke, cap). Minimum:

```csharp
using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class ApiTokensControllerTests
{
    private static ApiTokensController Build(Diariz.Domain.DiarizDbContext db, Guid userId)
        => new(db, new ApiTokenService()) { ControllerContext = Http.Context(userId.ToString()) };

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_AndStoresOnlyHash()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var res = await Build(db, user).Create(new CreateApiTokenRequest("CI"));

        var dto = Assert.IsType<ApiTokenCreatedDto>(Assert.IsType<ActionResult<ApiTokenCreatedDto>>(res).Value);
        Assert.StartsWith("dz_api_", dto.Token);
        var row = await db.ApiAccessTokens.SingleAsync();
        Assert.Equal(ApiTokenService.Hash(dto.Token), row.TokenHash);
        Assert.DoesNotContain(dto.Token, row.TokenHash);        // plaintext not stored
    }

    [Fact]
    public async Task List_ReturnsOwnTokens_WithoutSecret()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        await Build(db, user).Create(new CreateApiTokenRequest("one"));
        var list = await Build(db, user).List();
        Assert.Single(list);
        Assert.StartsWith("dz_api_", list[0].Prefix);
    }

    [Fact]
    public async Task Revoke_DeletesOwnToken()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var created = ((ActionResult<ApiTokenCreatedDto>)await Build(db, user).Create(new CreateApiTokenRequest("x"))).Value!;
        Assert.IsType<NoContentResult>(await Build(db, user).Revoke(created.Id));
        Assert.Empty(await db.ApiAccessTokens.ToListAsync());
    }
}
```

- [ ] **Step 3: Run it, verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokensControllerTests`
Expected: FAIL (compile — controller missing).

- [ ] **Step 4: Implement** (`ApiTokensController.cs`) — a copy of `McpTokensController.cs` with `Mcp`→`Api`, route `api/user/api-tokens`, default name `"API token"`, and `ApiAccessTokens`/`ApiAccessToken`:

```csharp
using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Manages the signed-in user's personal REST-API tokens. Authenticated with the normal JWT session;
/// the tokens it mints are a separate credential. The plaintext is returned once - only its hash is stored.</summary>
[ApiController]
[Authorize]
[Route("api/user/api-tokens")]
public class ApiTokensController : ControllerBase
{
    public const int MaxTokensPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IApiTokenService _tokens;

    public ApiTokensController(DiarizDbContext db, IApiTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<ApiTokenDto>> List() =>
        await _db.ApiAccessTokens
            .Where(t => t.UserId == UserId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new ApiTokenDto(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt))
            .ToListAsync();

    [HttpPost]
    public async Task<ActionResult<ApiTokenCreatedDto>> Create(CreateApiTokenRequest req)
    {
        var name = string.IsNullOrWhiteSpace(req.Name) ? "API token" : req.Name.Trim();
        if (name.Length > 128) name = name[..128];

        if (await _db.ApiAccessTokens.CountAsync(t => t.UserId == UserId) >= MaxTokensPerUser)
            return BadRequest("Token limit reached. Revoke an existing token before creating another.");

        var g = _tokens.Generate();
        var row = new ApiAccessToken { Id = Guid.NewGuid(), UserId = UserId, Name = name, TokenHash = g.Hash, Prefix = g.Prefix };
        _db.ApiAccessTokens.Add(row);
        await _db.SaveChangesAsync();
        return new ApiTokenCreatedDto(row.Id, row.Name, row.Prefix, g.Token);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (row is null) return NotFound();
        _db.ApiAccessTokens.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
```

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~ApiTokensControllerTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/ApiTokensController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/ApiTokensControllerTests.cs
git commit -m "feat(api-access): ApiTokensController CRUD (/api/user/api-tokens)"
```

---

## Task 7: Platform toggle in DTO + settings controller

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`, `src/Diariz.Api/Controllers/PlatformSettingsController.cs`
- Test: extend `tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs` (or create if absent)

- [ ] **Step 1: Add `ApiAccessEnabled` to the DTOs** — read the current `PlatformSettingsDto` (ApiDtos.cs:37) and `UpdatePlatformSettingsRequest` (ApiDtos.cs:40) and append a `bool ApiAccessEnabled` parameter to each (last positional field).

- [ ] **Step 2: Write the failing test** — a round-trip asserting the flag persists through `Update` → `Get`. Add to the platform-settings test class (mirror an existing quota round-trip test; construct the request with `ApiAccessEnabled: true` and assert `Get().ApiAccessEnabled == true`).

- [ ] **Step 3: Run it, verify it fails** (compile: DTO has no `ApiAccessEnabled`).

- [ ] **Step 4: Implement** — in `PlatformSettingsController`:
  - `Update`: add `s.ApiAccessEnabled = req.ApiAccessEnabled;` alongside the other assignments (line ~59).
  - `ToDto`: add `s.ApiAccessEnabled` as the final argument (line ~77).

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~PlatformSettings`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/PlatformSettingsController.cs tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs
git commit -m "feat(api-access): expose ApiAccessEnabled on platform settings"
```

---

## Task 8: Surface `apiAccessEnabled` on the user profile

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`UserProfileDto`), `src/Diariz.Api/Controllers/UserProfileController.cs`
- Test: extend `tests/Diariz.Api.Tests/UserProfileControllerTests.cs` (or the closest existing profile test)

- [ ] **Step 1: Add `bool ApiAccessEnabled` to `UserProfileDto`** (ApiDtos.cs:271) as the final field.

- [ ] **Step 2: Write the failing test** — seed `PlatformSettings { ApiAccessEnabled = true }`, GET the profile, assert `.ApiAccessEnabled == true` (and false when the row says false).

- [ ] **Step 3: Run it, verify it fails.**

- [ ] **Step 4: Implement** — inject `IPlatformSettingsService` into `UserProfileController` (add ctor param + field), then in `Get()` read it and pass the flag:

```csharp
    // add field + ctor param: private readonly IPlatformSettingsService _platform;
    // in Get(), before constructing the DTO:
    var apiEnabled = (await _platform.GetAsync()).ApiAccessEnabled;
    // ...append ApiAccessEnabled: apiEnabled as the final DTO argument.
```

- [ ] **Step 5: Run tests, verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/UserProfileController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/UserProfileControllerTests.cs
git commit -m "feat(api-access): surface apiAccessEnabled on the user profile"
```

---

## Task 9: Integration auth-matrix

**Files:** Create `tests/Diariz.Api.IntegrationTests/ApiAccessIntegrationTests.cs` (uses `ContainersFixture`, real Postgres). Model it on `McpTokensIntegrationTests.cs`.

- [ ] **Step 1: Write the tests.** Use the fixture's HTTP client / `CreateDbContext()`. Cover, against a known user-scoped GET endpoint (e.g. `GET /api/recordings`) and an admin endpoint (`GET /api/platform/settings`):

  - `dz_api_` token → **200** on `/api/recordings` **when `ApiAccessEnabled = true`**.
  - Same token → **401** when `ApiAccessEnabled = false`.
  - `dz_mcp_` token → **401** on `/api/recordings` (wrong credential for the general API).
  - `dz_api_` token → **401** on `/mcp` (the Mcp scheme rejects the `dz_api_` prefix).
  - A **JWT** session → **200** on `/api/recordings` (no regression).
  - An **admin user's** `dz_api_` token → **200** on `GET /api/platform/settings` (full parity: role claims present).

  Seed tokens by inserting `ApiAccessToken { TokenHash = ApiTokenService.Hash(plaintext) }` rows and sending `Authorization: Bearer <plaintext>`. Flip `PlatformSettings.ApiAccessEnabled` via the DbContext between cases.

- [ ] **Step 2: Run** (needs Docker)

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter FullyQualifiedName~ApiAccessIntegrationTests`
Expected: PASS (6 cases).

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/ApiAccessIntegrationTests.cs
git commit -m "test(api-access): integration auth matrix (JWT/api/mcp isolation + admin parity + gate)"
```

---

## Task 10: Web — api client + types

**Files:** Modify `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`

- [ ] **Step 1: Add types** (`types.ts`) — beside `McpToken`/`McpTokenCreated` (line ~326):

```typescript
export interface ApiToken { id: string; name: string; prefix: string; createdAt: string; lastUsedAt: string | null; }
export interface ApiTokenCreated { id: string; name: string; prefix: string; token: string; }
```
Add `apiAccessEnabled: boolean;` to `UserProfile` (line ~369) and to `PlatformSettings` (line ~280).

- [ ] **Step 2: Add client methods** (`api.ts`) — beside the MCP token methods (line ~636):

```typescript
  async listApiTokens(): Promise<ApiToken[]> {
    const { data } = await http.get<ApiToken[]>("/api/user/api-tokens");
    return data;
  },
  async createApiToken(name: string): Promise<ApiTokenCreated> {
    const { data } = await http.post<ApiTokenCreated>("/api/user/api-tokens", { name });
    return data;
  },
  async revokeApiToken(id: string): Promise<void> {
    await http.delete(`/api/user/api-tokens/${id}`);
  },
```
Import `ApiToken`, `ApiTokenCreated` in `api.ts`'s type import.

- [ ] **Step 3: Typecheck**

Run: `cd apps/web && npm run build`
Expected: build succeeds (types resolve).

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/lib/api.ts apps/web/src/lib/types.ts
git commit -m "feat(api-access): web api client + types for personal API tokens"
```

---

## Task 11: Web — `DeveloperAccessSection`

**Files:**
- Create: `apps/web/src/components/DeveloperAccessSection.tsx`
- Test: `apps/web/src/components/DeveloperAccessSection.test.tsx`
- Modify: `apps/web/src/locales/{en,es,fr,de}/account.json`

- [ ] **Step 1: Add i18n keys** to all four `account.json` (plain hyphens only). English values:

```json
"apiAccess": "API access",
"apiIntro": "Generate a personal token to call the Diariz API as yourself. The token is shown once - store it securely.",
"apiBaseUrl": "API base URL",
"apiCopyUrl": "Copy URL",
"apiTokenNamePlaceholder": "Token name (e.g. My script)",
"apiGenerate": "Generate token",
"apiTokenOnce": "Copy this token now - it won't be shown again.",
"apiCopyToken": "Copy token",
"apiShowExample": "Show example request",
"apiCopyExample": "Copy example",
"apiDefaultName": "API token",
"apiGenerateError": "Could not generate the token.",
"apiRevokeError": "Could not revoke the token.",
"apiRevoke": "Revoke",
"apiLastUsed": "used",
"apiNeverUsed": "never used",
"apiNoTokens": "No API tokens yet.",
"apiViewReference": "View API reference"
```
(Provide es/fr/de translations; no em/en dashes.)

- [ ] **Step 2: Write the failing test** (`DeveloperAccessSection.test.tsx`) — mirror the MCP section's test style (mock `../lib/api`, render inside a `QueryClientProvider`). Assert: create calls `api.createApiToken` and reveals the token once; revoke calls `api.revokeApiToken`.

```typescript
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { listApiTokens: vi.fn(), createApiToken: vi.fn(), revokeApiToken: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));
import { api } from "../lib/api";
import DeveloperAccessSection from "./DeveloperAccessSection";

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><DeveloperAccessSection /></QueryClientProvider>);
}

describe("DeveloperAccessSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listApiTokens as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.createApiToken as ReturnType<typeof vi.fn>).mockResolvedValue({ id: "t1", name: "x", prefix: "dz_api_ab12cd", token: "dz_api_secret" });
  });

  it("generates a token and shows it once", async () => {
    renderIt();
    fireEvent.click(await screen.findByRole("button", { name: /generate token/i }));
    await waitFor(() => expect(api.createApiToken).toHaveBeenCalled());
    expect(await screen.findByText("dz_api_secret")).toBeTruthy();
  });
});
```

- [ ] **Step 3: Run it, verify it fails** (component missing).

Run: `cd apps/web && npx vitest run src/components/DeveloperAccessSection.test.tsx`
Expected: FAIL.

- [ ] **Step 4: Implement** (`DeveloperAccessSection.tsx`) — adapt `McpAccessSection.tsx`: same layout, but for API tokens; replace the MCP config snippet with a copyable `curl` example, drop the OAuth-connections block. Full component:

```tsx
import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { ApiTokenCreated } from "../lib/types";

/// Preferences "Developers / API access" section: shows the API base URL, generates a personal API token
/// (shown once, with a ready-to-run curl example) and lists / revokes existing tokens. Shown only when the
/// platform has API access enabled (the parent gates on profile.apiAccessEnabled).
export default function DeveloperAccessSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: tokens } = useQuery({ queryKey: ["api-tokens"], queryFn: api.listApiTokens });

  const [name, setName] = useState("");
  const [created, setCreated] = useState<ApiTokenCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const baseUrl = `${window.location.origin}/api`;
  const example = `curl -H "Authorization: Bearer ${created?.token ?? "<your-token>"}" ${baseUrl}/recordings`;

  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  async function generate() {
    setError(null); setBusy(true);
    try {
      const tok = await api.createApiToken(name.trim() || t("apiDefaultName"));
      setCreated(tok); setName("");
      qc.invalidateQueries({ queryKey: ["api-tokens"] });
    } catch (e) { setError(apiErrorMessage(e, t("apiGenerateError"))); }
    finally { setBusy(false); }
  }

  async function revoke(id: string) {
    setError(null);
    try { await api.revokeApiToken(id); qc.invalidateQueries({ queryKey: ["api-tokens"] }); }
    catch (e) { setError(apiErrorMessage(e, t("apiRevokeError"))); }
  }

  const btn = "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("apiAccess")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("apiIntro")}</p>

      <div className="mt-2 flex items-center gap-2">
        <code className="flex-1 truncate rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800 dark:text-gray-200">{baseUrl}</code>
        <button type="button" onClick={() => copy(baseUrl)} className={btn}>{t("apiCopyUrl")}</button>
      </div>

      <div className="mt-2 flex gap-2">
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t("apiTokenNamePlaceholder")}
          aria-label={t("apiTokenNamePlaceholder")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100" />
        <button type="button" onClick={generate} disabled={busy} className={btn}>{t("apiGenerate")}</button>
      </div>

      {created && (
        <div className="mt-2 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
          <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("apiTokenOnce")}</p>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">{created.token}</code>
            <button type="button" onClick={() => copy(created.token)} className={btn}>{t("apiCopyToken")}</button>
          </div>
          <details className="mt-2">
            <summary className="cursor-pointer text-xs text-gray-600 dark:text-gray-300">{t("apiShowExample")}</summary>
            <pre className="mt-1 overflow-x-auto rounded bg-white p-2 text-[11px] leading-snug dark:bg-gray-900 dark:text-gray-200">{example}</pre>
            <button type="button" onClick={() => copy(example)} className={`mt-1 ${btn}`}>{t("apiCopyExample")}</button>
          </details>
        </div>
      )}

      <ul className="mt-2 space-y-1">
        {tokens?.map((tk) => (
          <li key={tk.id} className="flex items-center justify-between gap-2 text-xs text-gray-600 dark:text-gray-300">
            <span className="truncate">{tk.name} · <code className="text-gray-500 dark:text-gray-400">{tk.prefix}…</code> · {tk.lastUsedAt ? t("apiLastUsed") : t("apiNeverUsed")}</span>
            <button type="button" onClick={() => revoke(tk.id)} className="shrink-0 text-red-600 hover:underline dark:text-red-400">{t("apiRevoke")}</button>
          </li>
        ))}
        {tokens?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("apiNoTokens")}</li>}
      </ul>

      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
```

> Note: the `apiViewReference` button/link to the docs route is added in **PR B** (the docs don't exist yet).

- [ ] **Step 5: Run tests, verify pass**

Run: `cd apps/web && npx vitest run src/components/DeveloperAccessSection.test.tsx && npx vitest run src/locales.test.ts`
Expected: PASS (component + i18n parity).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/DeveloperAccessSection.tsx apps/web/src/components/DeveloperAccessSection.test.tsx apps/web/src/locales
git commit -m "feat(api-access): Developer API access section (token management)"
```

---

## Task 12: Web — Preferences "Developers" tab (gated on `apiAccessEnabled`)

**Files:** Modify `apps/web/src/components/PreferencesModal.tsx`, `apps/web/src/locales/{en,es,fr,de}/account.json`

- [ ] **Step 1: Add the tab label i18n key** to all four `account.json`: `"tabDevelopers": "Developers"` (translate).

- [ ] **Step 2: Wire the tab.** In `PreferencesModal.tsx`:
  - Import: `import DeveloperAccessSection from "./DeveloperAccessSection";` and `import { api } from "../lib/api";` (if not present) and the `useQuery` hook.
  - Extend the type: `export type PreferencesTab = "profile" | "google" | "feeds" | "claude" | "voiceprints" | "developers";`
  - Read the profile to gate the tab: `const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });`
  - In the `tabs` array, conditionally include the Developers entry: `...(profile?.apiAccessEnabled ? [{ id: "developers" as const, label: t("tabDevelopers") }] : [])` (place after `claude`).
  - In the section switch (line ~84): add `{tab === "developers" && <DeveloperAccessSection />}`.

- [ ] **Step 3: Typecheck + run web tests**

Run: `cd apps/web && npm run build && npx vitest run`
Expected: build + all tests pass.

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/components/PreferencesModal.tsx apps/web/src/locales
git commit -m "feat(api-access): Developers tab in Preferences (shown when enabled)"
```

---

## Task 13: Web — platform "Integration" tab with the enable switch

**Files:** Modify `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/locales/{en,es,fr,de}/account.json`

- [ ] **Step 1: Add i18n keys** to all four `account.json`: `"integrationTab": "Integration"`, `"apiAccessEnabledLabel": "Enable user API access"`, `"apiAccessEnabledHelp": "Let users generate personal API tokens and call the Diariz API. Off by default."` (translate).

- [ ] **Step 2: Write the failing test** — extend `SettingsModal.test.tsx`: as a Platform Admin, the Integration tab is present and toggling the switch + saving calls `api.updatePlatformSettings` with `apiAccessEnabled: true`. (Mirror the existing minutes-generation/admin-tab test in that file.)

- [ ] **Step 3: Run it, verify it fails.**

- [ ] **Step 4: Implement** in `SettingsModal.tsx`:
  - Extend `type Tab` (line 10): add `"integration"`.
  - Add a `TabButton` for `integration` inside the `isPlatformAdmin` block (beside Quotas/Maintenance, line ~162).
  - Render an `integration` panel: a checkbox bound to the platform-settings form state field `apiAccessEnabled` (add it to the local state that seeds from `getPlatformSettings` and is sent by the existing `updatePlatformSettings` call in `onOk`). Label/help from the new keys.
  - Ensure the `updatePlatformSettings` body includes `apiAccessEnabled` (the `PlatformSettings` type now has it — Task 10).

- [ ] **Step 5: Run tests, verify pass**

Run: `cd apps/web && npx vitest run src/components/SettingsModal.test.tsx && npx vitest run src/locales.test.ts`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/SettingsModal.tsx apps/web/src/locales
git commit -m "feat(api-access): platform Integration tab with API-access enable switch"
```

---

## Task 14: Docs, version, release notes + full verification

**Files:** `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/lib/releases.ts`, `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `apps/desktop/package-lock.json`, `apps/web/package-lock.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts` `CAPABILITIES`

- [ ] **Step 1: Update `Data_Schema.md`** — add the `ApiAccessTokens` table (columns Id, UserId FK cascade, Name, TokenHash unique, Prefix, CreatedAt, LastUsedAt) and the `PlatformSettings.ApiAccessEnabled` column; add the migration to the migration-history table.

- [ ] **Step 2: Update `Overall_Synopsis_of_Platform.md`** — a short subsection: personal REST-API tokens (`dz_api_`, hashed, full-parity principal), the forwarding default auth scheme, and the `ApiAccessEnabled` platform gate (default off).

- [ ] **Step 3: Update `CAPABILITIES`** in `releases.ts` — one sentence that users can generate a personal API token (when a Platform Admin has enabled API access) to call the Diariz API.

- [ ] **Step 4: Bump version to `0.101.0`** (functional enhancement -> Minor +1, Build 0) across `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` `<Version>`, and regenerate both lockfiles (`npm install --package-lock-only` in `apps/web` and `apps/desktop`).

- [ ] **Step 5: Add the `RELEASES[0]` entry** (`releases.ts`) for `0.101.0`, `pr` = the PR number, headline about personal API tokens + the platform enable switch, `added` bullets, and note "Server redeploy (web + API); no desktop release."

- [ ] **Step 6: Full verification**

Run: `dotnet build Diariz.slnx` (expect 0 errors) ; `dotnet test tests/Diariz.Api.Tests` (all pass) ; `dotnet test tests/Diariz.Api.IntegrationTests --filter FullyQualifiedName~ApiAccessIntegrationTests` (Docker; pass) ; `cd apps/web && npm run build && npx vitest run` (pass, incl. `releases.test.ts` asserting `RELEASES[0].version === version.json`).

- [ ] **Step 7: Live verification** (per the repo's verify practice, non-destructive): with the stack running, enable API access via the Integration tab (or set `PlatformSettings.ApiAccessEnabled=true`), generate a token in the Developers tab, then `curl -H "Authorization: Bearer <dz_api_…>" http://localhost:8080/api/recordings` → 200 JSON; toggle the switch off → the same call returns 401. Confirm the Developers tab hides when disabled.

- [ ] **Step 8: Commit**

```bash
git add docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md apps/web/src/lib/releases.ts version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj
git commit -m "docs+release: user API access (personal tokens + platform gate) 0.101.0"
```

---

## Deploy surface

Server redeploy (web + API). One DB migration (`AddApiAccessTokens`) auto-applies on API start. No worker rebuild, no desktop release. Feature ships **disabled** (a Platform Admin enables it in Settings -> Integration).

## Out of scope (PR B)

Curated production OpenAPI document + in-app Scalar reference at `/developers/api`, and wiring the "View API reference" links into the Developers tab and the Integration tab. Separate plan after PR A merges.
