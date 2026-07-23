# Integrations Phase 2: Outbound Webhooks (core) - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user register personal outbound webhook subscriptions ("Automations") that receive signed HTTP events when their recordings and formula results change state, delivered reliably via a Postgres-backed queue + retry worker.

**Architecture:** Two new entities (`WebhookSubscription`, `WebhookDelivery`). At the existing SignalR notify call-sites, an `IWebhookPublisher` matches the owner's active subscriptions and inserts `WebhookDelivery` rows (thin JSON payload, stored as text to preserve exact signed bytes). A `WebhookDeliveryWorker` (BackgroundService, polls the delivery table like `AudioRetentionWorker`) POSTs each due delivery with Standard-Webhooks HMAC signing, applies exponential backoff on failure, and auto-disables a subscription after repeated failures. A `WebhooksController` manages subscriptions (gated on the platform `WebhooksEnabled` toggle, SSRF-validated URLs, signing secret shown once). The web app gets an "Automations" Preferences tab.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, ASP.NET Data Protection, xUnit (unit + Testcontainers integration), React 19 + TS + Vite + Tailwind v4, vitest, i18next (en/de/es/fr).

This is **Phase 2 of 3**. Phase 1 (platform toggles + scoped tokens) shipped in v0.151.0. Phase 3 (Workflow Signals + platform-scoped subscriptions + inline output routing) follows. Phase 2 ships personal subscriptions only.

Source spec: `docs/superpowers/specs/2026-07-23-integrations-design.md` (sections 7-10, 12-13). Phase 1 plan (conventions): `docs/superpowers/plans/2026-07-23-integrations-phase1-foundation.md`.

## Global Constraints

- **TDD:** failing test first, watch it fail, then minimal code. No production code without a preceding red test.
- **No em/en dashes in user-facing copy** (UI strings, all four locale catalogs, release notes). Plain hyphen `-`. Internal docs/code exempt.
- **i18n:** every new user-facing key is added to `apps/web/src/locales/{en,de,es,fr}/account.json` in all four languages, with correct native spelling (German umlauts, Spanish/French accents) and no fancy dashes.
- **Signed-body integrity:** `WebhookDelivery.PayloadJson` MUST be a plain `text` column, NOT `jsonb` - jsonb reformats JSON and would invalidate the HMAC signature computed over the exact bytes.
- **Personal scope only:** Phase 2 subscriptions fire only for events on recordings the subscribing user owns. The `WebhookSubscription.Scope` column defaults to `Personal`; the `Platform` value + signal routing are Phase 3.
- **Best-effort publishing:** emitting a webhook event at a notify call-site must never throw into or break the originating request/worker (wrap in try/catch, log on failure).
- **Platform gate:** every user-facing webhook action + the Automations UI is gated on `PlatformSettings.WebhooksEnabled` (default off, added in Phase 1). Checked in the controller via `IPlatformSettingsService` (webhooks have no authenticator to gate).
- **SSRF:** user-supplied target URLs are validated (scheme https/http, DNS-resolve, reject loopback/private/link-local/CGNAT/metadata) reusing `UrlFetchGuard`; the delivery HttpClient sets `AllowAutoRedirect = false`.
- **New tables, so migration column defaults need no hand-editing** (no pre-existing rows to backfill) - unlike Phase 1.
- **Versioning:** functional enhancement -> Minor bump `0.152.0` -> `0.153.0` (0.152.0 shipped separately via PR #334), mirrored to `version.json` + `apps/web/package.json` + `apps/desktop/package.json` + `src/Diariz.Api/Diariz.Api.csproj`, `RELEASES[0]` == `version.json`.
- **Deployment surface:** server redeploy only (no desktop). Migration additive + forward-restore-safe (no `CurrentFormat` bump). Requires `App:PublicUrl` configured in any deployment that enables webhooks (the delivery worker has no request context to derive an origin).

---

## File Structure

**Backend - create:**
- `src/Diariz.Domain/Entities/WebhookSubscription.cs`, `WebhookDelivery.cs`, `WebhookScope.cs`, `WebhookDeliveryStatus.cs`.
- `src/Diariz.Domain/Migrations/<ts>_AddWebhooks.cs` (generated).
- `src/Diariz.Api/Webhooks/WebhookEventTypes.cs` - event-type constants + CSV membership helper (pure).
- `src/Diariz.Api/Webhooks/WebhookSigner.cs` - Standard-Webhooks HMAC signing (pure).
- `src/Diariz.Api/Webhooks/WebhookBackoff.cs` - retry schedule (pure).
- `src/Diariz.Api/Webhooks/WebhookPayload.cs` - envelope builder (pure) + `WebhookLinks`.
- `src/Diariz.Api/Services/WebhookSecretProtector.cs` - Data-Protection protector (`IWebhookSecretProtector`).
- `src/Diariz.Api/Services/WebhookUrlValidator.cs` - `IWebhookUrlValidator` (SSRF, DNS seam).
- `src/Diariz.Api/Services/WebhookPublisher.cs` - `IWebhookPublisher` (matching + delivery-row insert).
- `src/Diariz.Api/Services/WebhookDeliveryProcessor.cs` - processes due deliveries (POST, sign, backoff, auto-disable).
- `src/Diariz.Api/Services/WebhookDeliveryWorker.cs` - `BackgroundService` poll loop.
- `src/Diariz.Api/Controllers/WebhooksController.cs`.

**Backend - modify:**
- `src/Diariz.Domain/DiarizDbContext.cs` - two DbSets + model config.
- `src/Diariz.Api/Contracts/ApiDtos.cs` - webhook DTOs + add `WebhooksEnabled` to `UserProfileDto`.
- `src/Diariz.Api/Controllers/UserProfileController.cs` - project `WebhooksEnabled`.
- `src/Diariz.Api/Controllers/RecordingsController.cs` - emit `recording.created`.
- `src/Diariz.Api/Controllers/WorkerCallbackController.cs` - emit `recording.transcribed` / `.transcription_failed`.
- `src/Diariz.Api/Services/FormulaRunProcessor.cs` + `FormulaRunWorker.cs` - emit `formula_result.completed` / `.failed`.
- `src/Diariz.Api/Configuration/AppOptions.cs` - `WebhookOptions`.
- `src/Diariz.Api/Program.cs` - DI: options, protector, validator, publisher, processor, named HttpClient, hosted worker.

**Web - modify:**
- `apps/web/src/lib/types.ts` - webhook types + `UserProfile.webhooksEnabled`.
- `apps/web/src/lib/api.ts` - webhook client methods.
- `apps/web/src/components/AutomationsSection.tsx` (create) - the panel.
- `apps/web/src/components/PreferencesModal.tsx` - the gated "Automations" tab.
- `apps/web/src/locales/{en,de,es,fr}/account.json` - strings.

**Docs / version:** `version.json` + 3 mirrors; `releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`; `docs/Data_Schema.md`.

---

## Task 1: Webhook entities + migration (schema)

**Files:**
- Create: `src/Diariz.Domain/Entities/WebhookScope.cs`, `WebhookDeliveryStatus.cs`, `WebhookSubscription.cs`, `WebhookDelivery.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Create: `src/Diariz.Domain/Migrations/<ts>_AddWebhooks.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/WebhookSchemaTests.cs`

**Interfaces produced:** the two entities + two enums; DbSets `Webhooks` and `WebhookDeliveries`.

- [ ] **Step 1: Write the failing test**

`tests/Diariz.Api.IntegrationTests/WebhookSchemaTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WebhookSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task Subscription_and_delivery_round_trip_and_cascade_on_user_delete()
    {
        await using var db = fx.CreateDbContext();
        var userId = await SeedUser(db);

        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "Zap", Url = "https://hooks.example.com/x",
            SecretEncrypted = "cipher", EventTypes = "recording.transcribed,formula_result.completed",
        };
        db.Webhooks.Add(sub);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_1", EventType = "recording.transcribed",
            PayloadJson = "{\"id\":\"evt_1\"}", NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Webhooks.SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(WebhookScope.Personal, reloaded.Scope);
        Assert.True(reloaded.IsActive);
        Assert.Equal(WebhookDeliveryStatus.Pending, (await db.WebhookDeliveries.SingleAsync()).Status);

        // Deleting the owning user cascades the subscription and its deliveries.
        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
        await db.SaveChangesAsync();
        Assert.Empty(await db.Webhooks.ToListAsync());
        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }

    private static async Task<Guid> SeedUser(DiarizDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = $"{id:N}@e.com", UserName = $"{id:N}@e.com" });
        await db.SaveChangesAsync();
        return id;
    }
}
```

> If a sibling integration test already has a user-seed helper, use it instead of the private one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~WebhookSchemaTests"`
Expected: FAIL to compile (entities/DbSets missing).

- [ ] **Step 3: Create the enums**

`src/Diariz.Domain/Entities/WebhookScope.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Which recordings a subscription fires for. Append only - values persist as ints.</summary>
public enum WebhookScope
{
    /// <summary>Fires only for events on recordings the subscribing user owns.</summary>
    Personal = 0,

    /// <summary>Admin-owned, signal-routed, fires across users. Phase 3.</summary>
    Platform = 1,
}
```

`src/Diariz.Domain/Entities/WebhookDeliveryStatus.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of a single webhook delivery attempt-set. Append only - values persist as ints.</summary>
public enum WebhookDeliveryStatus
{
    Pending = 0,   // due for delivery / retrying
    Delivered = 1, // a 2xx was received
    Failed = 2,    // exhausted the retry schedule
}
```

- [ ] **Step 4: Create the entities**

`src/Diariz.Domain/Entities/WebhookSubscription.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A user-registered outbound webhook ("Automation"). Fires signed HTTP events to <see cref="Url"/>
/// when the owner's recordings/formula results change state.</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public ApplicationUser? Owner { get; set; }

    /// <summary>Personal (Phase 2) or Platform (Phase 3). Defaults to Personal.</summary>
    public WebhookScope Scope { get; set; } = WebhookScope.Personal;

    public string Name { get; set; } = string.Empty;

    /// <summary>Delivery target. https required (http only for localhost in dev); SSRF-validated on write.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The HMAC signing secret, encrypted at rest (Data Protection). Shown to the user once.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>Comma-separated event-type keys this subscription wants (see WebhookEventTypes).</summary>
    public string EventTypes { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Consecutive failed deliveries; reset to 0 on any success. Auto-disable at the threshold.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Set when auto-disabled so the UI can explain why.</summary>
    public string? DisabledReason { get; set; }

    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/Diariz.Domain/Entities/WebhookDelivery.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>One event queued for delivery to one subscription. Doubles as the retry queue and the audit log.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscription? Subscription { get; set; }

    /// <summary>Stable idempotency key (the `webhook-id` header); constant across retries of this delivery.</summary>
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>The exact signed request body. Stored as plain text (NOT jsonb) so the bytes - and therefore
    /// the HMAC signature computed over them - are preserved verbatim across retries.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int AttemptCount { get; set; }

    /// <summary>Earliest time the worker may attempt this delivery. The poll key.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public int? ResponseStatus { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Register DbSets + model config**

In `src/Diariz.Domain/DiarizDbContext.cs`, add near the other DbSets:

```csharp
    public DbSet<WebhookSubscription> Webhooks => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
```

In `OnModelCreating` (provider-agnostic - keep OUTSIDE the `isNpgsql` guard so the in-memory test provider loads it; these are plain columns, no jsonb/vector):

```csharp
        modelBuilder.Entity<WebhookSubscription>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.Url).HasMaxLength(2048);
            e.HasIndex(s => s.OwnerUserId);
            e.HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.Property(d => d.EventId).HasMaxLength(64);
            e.Property(d => d.EventType).HasMaxLength(64);
            e.HasIndex(d => new { d.Status, d.NextAttemptAt }); // the worker's due-poll index
            e.HasOne(d => d.Subscription).WithMany().HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddWebhooks --project src/Diariz.Domain --startup-project src/Diariz.Api`

These are NEW tables (no pre-existing rows), so the EF-scaffolded defaults need no hand-editing. Just confirm `Up` creates both tables with the FK cascade + the `(Status, NextAttemptAt)` index, and `Down` drops them.

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~WebhookSchemaTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/Webhook*.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/WebhookSchemaTests.cs
git commit -m "feat: webhook subscription + delivery schema"
```

---

## Task 2: Signing, backoff, event types, envelope (pure)

**Files:**
- Create: `src/Diariz.Api/Webhooks/WebhookEventTypes.cs`, `WebhookSigner.cs`, `WebhookBackoff.cs`, `WebhookPayload.cs`
- Test: `tests/Diariz.Api.Tests/WebhookSignerTests.cs`, `WebhookBackoffTests.cs`, `WebhookEventTypesTests.cs`

**Interfaces produced:**
- `WebhookEventTypes` constants (`RecordingCreated`, `RecordingTranscribed`, `RecordingTranscriptionFailed`, `FormulaResultCompleted`, `FormulaResultFailed`, `Ping`) + `bool Matches(string csv, string type)` + `string[] Split(string csv)` + `string Join(IEnumerable<string>)`.
- `string WebhookSigner.Sign(string secret, string webhookId, long timestampUnix, string body)`.
- `WebhookBackoff.MaxAttempts` (const 8) + `TimeSpan NextDelay(int attemptCount)`.
- `string WebhookPayload.Build(string eventId, string type, DateTimeOffset createdUtc, object data)`; `WebhookLinks.For(string publicUrl, Guid recordingId)`.

- [ ] **Step 1: Write the failing tests**

`tests/Diariz.Api.Tests/WebhookSignerTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookSignerTests
{
    [Fact]
    public void Sign_matches_standard_webhooks_hmac_vector()
    {
        // v1,<base64(HMAC-SHA256(secret, "id.timestamp.body"))>
        const string secret = "s3cr3t";
        const string id = "evt_abc";
        const long ts = 1700000000;
        const string body = "{\"hello\":\"world\"}";
        var expectedMac = Convert.ToBase64String(
            new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(
                Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));

        Assert.Equal($"v1,{expectedMac}", WebhookSigner.Sign(secret, id, ts, body));
    }
}
```

`tests/Diariz.Api.Tests/WebhookBackoffTests.cs`:

```csharp
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookBackoffTests
{
    [Fact]
    public void Schedule_is_monotonic_and_bounded()
    {
        Assert.Equal(8, WebhookBackoff.MaxAttempts);
        var delays = Enumerable.Range(1, WebhookBackoff.MaxAttempts).Select(WebhookBackoff.NextDelay).ToList();
        for (var i = 1; i < delays.Count; i++)
            Assert.True(delays[i] >= delays[i - 1], "delays must be non-decreasing");
        Assert.True(delays[0] <= TimeSpan.FromSeconds(30));   // first retry is soon
        Assert.True(delays[^1] >= TimeSpan.FromHours(1));     // last retry is far out
    }
}
```

`tests/Diariz.Api.Tests/WebhookEventTypesTests.cs`:

```csharp
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookEventTypesTests
{
    [Fact]
    public void Matches_finds_a_type_in_the_csv()
    {
        var csv = WebhookEventTypes.Join(new[]
            { WebhookEventTypes.RecordingTranscribed, WebhookEventTypes.FormulaResultCompleted });
        Assert.True(WebhookEventTypes.Matches(csv, WebhookEventTypes.RecordingTranscribed));
        Assert.False(WebhookEventTypes.Matches(csv, WebhookEventTypes.RecordingCreated));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Webhook"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the pure helpers**

`src/Diariz.Api/Webhooks/WebhookEventTypes.cs`:

```csharp
namespace Diariz.Api.Webhooks;

/// <summary>Canonical outbound event-type keys (v1) and CSV membership helpers. Subscriptions store the set
/// of wanted types as a comma-separated string in <c>WebhookSubscription.EventTypes</c>.</summary>
public static class WebhookEventTypes
{
    public const string RecordingCreated = "recording.created";
    public const string RecordingTranscribed = "recording.transcribed";
    public const string RecordingTranscriptionFailed = "recording.transcription_failed";
    public const string FormulaResultCompleted = "formula_result.completed";
    public const string FormulaResultFailed = "formula_result.failed";
    public const string Ping = "webhook.ping"; // test-only, never subscribable

    /// <summary>The types a user may subscribe to (excludes the internal ping).</summary>
    public static readonly IReadOnlyList<string> Subscribable = new[]
    {
        RecordingCreated, RecordingTranscribed, RecordingTranscriptionFailed,
        FormulaResultCompleted, FormulaResultFailed,
    };

    public static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> types) => string.Join(',', types);

    public static bool Matches(string? csv, string type) =>
        Split(csv).Contains(type, StringComparer.Ordinal);
}
```

`src/Diariz.Api/Webhooks/WebhookSigner.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Webhooks;

/// <summary>Standard-Webhooks signing: <c>v1,base64(HMAC-SHA256(secret, "id.timestamp.body"))</c>.</summary>
public static class WebhookSigner
{
    public static string Sign(string secret, string webhookId, long timestampUnix, string body)
    {
        var signed = $"{webhookId}.{timestampUnix}.{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return $"v1,{Convert.ToBase64String(mac)}";
    }
}
```

`src/Diariz.Api/Webhooks/WebhookBackoff.cs`:

```csharp
namespace Diariz.Api.Webhooks;

/// <summary>Exponential-ish retry schedule: ~8 attempts spread over ~24h (Standard-Webhooks style).</summary>
public static class WebhookBackoff
{
    public const int MaxAttempts = 8;

    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(5), TimeSpan.FromHours(10),
    };

    /// <summary>Delay before the given (1-based) attempt number. Clamped to the last entry.</summary>
    public static TimeSpan NextDelay(int attemptCount)
    {
        var i = Math.Clamp(attemptCount - 1, 0, Delays.Length - 1);
        return Delays[i];
    }
}
```

`src/Diariz.Api/Webhooks/WebhookPayload.cs`:

```csharp
using System.Text.Json;

namespace Diariz.Api.Webhooks;

/// <summary>Absolute links included in a webhook payload's <c>data.links</c>.</summary>
public sealed record WebhookLinks(string Api, string Web);

/// <summary>Builds the thin outbound envelope <c>{ id, type, created, data }</c> as a compact JSON string.
/// The returned string is the EXACT body that gets signed and stored - do not re-serialize it downstream.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Build(string eventId, string type, DateTimeOffset createdUtc, object data) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type,
            created = createdUtc.ToUniversalTime().ToString("o"),
            data,
        }, Options);

    public static WebhookLinks For(string publicUrl, Guid recordingId)
    {
        var baseUrl = publicUrl.TrimEnd('/');
        return new WebhookLinks($"{baseUrl}/api/recordings/{recordingId}", $"{baseUrl}/recordings/{recordingId}");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Webhook"`
Expected: PASS (3 classes).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Webhooks tests/Diariz.Api.Tests/WebhookSignerTests.cs tests/Diariz.Api.Tests/WebhookBackoffTests.cs tests/Diariz.Api.Tests/WebhookEventTypesTests.cs
git commit -m "feat: webhook signing, backoff, event types, envelope (pure)"
```

---

## Task 3: SSRF webhook URL validator

**Files:**
- Create: `src/Diariz.Api/Services/WebhookUrlValidator.cs`
- Modify: `src/Diariz.Api/Program.cs` (register)
- Test: `tests/Diariz.Api.Tests/WebhookUrlValidatorTests.cs`

**Interfaces produced:** `record WebhookUrlValidation(bool Ok, string? Reason)`; `IWebhookUrlValidator { Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default); }`.

**Consumes:** `UrlFetchGuard` (`src/Diariz.Api/Services/UrlFetcher.cs`) - `IsAllowedScheme(Uri)`, `IsBlocked(IPAddress)`.

- [ ] **Step 1: Write the failing test** (DNS is injected as a seam for determinism)

`tests/Diariz.Api.Tests/WebhookUrlValidatorTests.cs`:

```csharp
using System.Net;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class WebhookUrlValidatorTests
{
    private static WebhookUrlValidator With(params string[] ips) =>
        new((_, _) => Task.FromResult(ips.Select(IPAddress.Parse).ToArray()));

    [Fact]
    public async Task Rejects_non_http_scheme()
    {
        var r = await With("1.2.3.4").ValidateAsync("ftp://example.com/x");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Rejects_unparseable_url()
    {
        var r = await With("1.2.3.4").ValidateAsync("not a url");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Rejects_private_and_loopback_targets()
    {
        Assert.False((await With("127.0.0.1").ValidateAsync("https://internal.local/x")).Ok);
        Assert.False((await With("10.0.0.5").ValidateAsync("https://internal.local/x")).Ok);
        Assert.False((await With("169.254.169.254").ValidateAsync("https://metadata/x")).Ok);
    }

    [Fact]
    public async Task Allows_a_public_https_target()
    {
        var r = await With("93.184.216.34").ValidateAsync("https://hooks.example.com/abc");
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
    }

    [Fact]
    public async Task Rejects_when_any_resolved_ip_is_blocked()
    {
        // A public A-record plus a private one (DNS-rebinding style) must be rejected.
        var r = await With("93.184.216.34", "10.1.2.3").ValidateAsync("https://sneaky.example.com/x");
        Assert.False(r.Ok);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookUrlValidatorTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the validator**

`src/Diariz.Api/Services/WebhookUrlValidator.cs`:

```csharp
using System.Net;

namespace Diariz.Api.Services;

public sealed record WebhookUrlValidation(bool Ok, string? Reason)
{
    public static readonly WebhookUrlValidation Valid = new(true, null);
    public static WebhookUrlValidation Invalid(string reason) => new(false, reason);
}

public interface IWebhookUrlValidator
{
    Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default);
}

/// <summary>Validates a user-supplied webhook target against SSRF: http(s) only, and every DNS-resolved IP must
/// pass <see cref="UrlFetchGuard.IsBlocked"/> (rejects loopback/private/link-local/CGNAT/metadata). The DNS
/// resolver is injectable for tests.</summary>
public sealed class WebhookUrlValidator : IWebhookUrlValidator
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public WebhookUrlValidator() : this((host, ct) => Dns.GetHostAddressesAsync(host, ct)) { }
    public WebhookUrlValidator(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => _resolve = resolve;

    public async Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return WebhookUrlValidation.Invalid("Enter a valid URL.");
        if (!UrlFetchGuard.IsAllowedScheme(uri))
            return WebhookUrlValidation.Invalid("The URL must start with http:// or https://.");

        IPAddress[] ips;
        try { ips = await _resolve(uri.DnsSafeHost, ct); }
        catch { return WebhookUrlValidation.Invalid("Could not resolve that host."); }

        if (ips.Length == 0 || ips.Any(UrlFetchGuard.IsBlocked))
            return WebhookUrlValidation.Invalid("That address is not allowed.");

        return WebhookUrlValidation.Valid;
    }
}
```

- [ ] **Step 4: Register it**

In `src/Diariz.Api/Program.cs`, near the other service registrations:

```csharp
builder.Services.AddSingleton<IWebhookUrlValidator, WebhookUrlValidator>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookUrlValidatorTests"`
Expected: PASS. Then `dotnet build Diariz.slnx`.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/WebhookUrlValidator.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/WebhookUrlValidatorTests.cs
git commit -m "feat: SSRF validator for webhook target URLs"
```

---

## Task 4: Secret protector + publisher (matching + delivery-row insert)

**Files:**
- Create: `src/Diariz.Api/Services/WebhookSecretProtector.cs`, `src/Diariz.Api/Services/WebhookPublisher.cs`
- Modify: `src/Diariz.Api/Program.cs` (register both)
- Test: `tests/Diariz.Api.Tests/WebhookPublisherTests.cs`

**Interfaces produced:**
- `IWebhookSecretProtector { string? Protect(string?); string? Unprotect(string?); }`.
- `IWebhookPublisher { Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default); }`.

**Consumes:** `WebhookEventTypes`, `WebhookPayload` (Task 2); the entities (Task 1).

- [ ] **Step 1: Write the failing test**

`tests/Diariz.Api.Tests/WebhookPublisherTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class WebhookPublisherTests
{
    private static WebhookSubscription Sub(Guid owner, string events, bool active = true) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = owner, Name = "s", Url = "https://x/y",
        SecretEncrypted = "c", EventTypes = events, IsActive = active,
    };

    [Fact]
    public async Task Publishes_one_delivery_per_matching_active_subscription()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Webhooks.Add(Sub(owner, "recording.transcribed,formula_result.completed"));   // matches
        db.Webhooks.Add(Sub(owner, "recording.created"));                                 // wrong type
        db.Webhooks.Add(Sub(owner, "recording.transcribed", active: false));             // inactive
        db.Webhooks.Add(Sub(other, "recording.transcribed"));                            // wrong owner
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingTranscribed, owner,
            new { recordingId = Guid.NewGuid(), status = "Transcribed" });

        var deliveries = await db.WebhookDeliveries.ToListAsync();
        Assert.Single(deliveries);
        Assert.Equal(WebhookEventTypes.RecordingTranscribed, deliveries[0].EventType);
        Assert.Equal(WebhookDeliveryStatus.Pending, deliveries[0].Status);
        Assert.Contains("\"type\":\"recording.transcribed\"", deliveries[0].PayloadJson);
        Assert.False(string.IsNullOrEmpty(deliveries[0].EventId));
    }

    [Fact]
    public async Task No_subscribers_inserts_nothing()
    {
        var db = TestDb.Create();
        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingCreated, Guid.NewGuid(), new { });
        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookPublisherTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the protector**

`src/Diariz.Api/Services/WebhookSecretProtector.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Services;

public interface IWebhookSecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? ciphertext);
}

/// <summary>Encrypts webhook signing secrets at rest (must be recoverable to sign), mirroring
/// <see cref="ApiKeyProtector"/> with a distinct purpose string.</summary>
public sealed class WebhookSecretProtector : IWebhookSecretProtector
{
    private readonly IDataProtector _protector;

    public WebhookSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Diariz.Webhooks.SigningSecret");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Implement the publisher**

`src/Diariz.Api/Services/WebhookPublisher.cs`:

```csharp
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IWebhookPublisher
{
    /// <summary>Builds the thin envelope once and inserts one <see cref="WebhookDelivery"/> per matching active
    /// personal subscription owned by <paramref name="ownerUserId"/>. Best-effort: never throws.</summary>
    Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default);
}

public sealed class WebhookPublisher : IWebhookPublisher
{
    private readonly DiarizDbContext _db;
    private readonly ILogger<WebhookPublisher> _log;

    public WebhookPublisher(DiarizDbContext db, ILogger<WebhookPublisher> log) { _db = db; _log = log; }

    public async Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default)
    {
        try
        {
            var subs = await _db.Webhooks
                .Where(s => s.IsActive && s.Scope == WebhookScope.Personal && s.OwnerUserId == ownerUserId)
                .ToListAsync(ct);
            var matches = subs.Where(s => WebhookEventTypes.Matches(s.EventTypes, eventType)).ToList();
            if (matches.Count == 0) return;

            var eventId = "evt_" + Guid.NewGuid().ToString("N");
            var body = WebhookPayload.Build(eventId, eventType, DateTimeOffset.UtcNow, data);

            foreach (var s in matches)
            {
                _db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = Guid.NewGuid(), SubscriptionId = s.Id, EventId = eventId, EventType = eventType,
                    PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Publishing must never break the originating request/worker.
            _log.LogError(ex, "Failed to enqueue webhook deliveries for {EventType}", eventType);
        }
    }
}
```

- [ ] **Step 5: Register both**

In `src/Diariz.Api/Program.cs`:

```csharp
builder.Services.AddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
builder.Services.AddScoped<IWebhookPublisher, WebhookPublisher>();
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookPublisherTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/WebhookSecretProtector.cs src/Diariz.Api/Services/WebhookPublisher.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/WebhookPublisherTests.cs
git commit -m "feat: webhook secret protector and delivery publisher"
```

---

## Task 5: Delivery processor + worker (POST, sign, retry, auto-disable)

**Files:**
- Create: `src/Diariz.Api/Services/WebhookDeliveryProcessor.cs`, `src/Diariz.Api/Services/WebhookDeliveryWorker.cs`
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs` (WebhookOptions), `src/Diariz.Api/Program.cs` (options, named HttpClient, hosted service)
- Test: `tests/Diariz.Api.Tests/WebhookDeliveryProcessorTests.cs`

**Interfaces produced:** `WebhookOptions { int AutoDisableThreshold=15; int BatchSize=20; }`; `WebhookDeliveryProcessor.ProcessDueAsync(DiarizDbContext db, HttpClient http, DateTimeOffset now, CancellationToken ct)`.

**Consumes:** `WebhookSigner`, `WebhookBackoff` (Task 2); `IWebhookSecretProtector` (Task 4); the entities (Task 1).

- [ ] **Step 1: Write the failing test** (fake `HttpMessageHandler` records the request + returns a scripted status)

`tests/Diariz.Api.Tests/WebhookDeliveryProcessorTests.cs`:

```csharp
using System.Net;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class WebhookDeliveryProcessorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        public StubHandler(HttpStatusCode status) => _status = status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status);
        }
    }

    private sealed class PlainProtector : IWebhookSecretProtector
    {
        public string? Protect(string? p) => p;
        public string? Unprotect(string? c) => c; // secret stored as plaintext in these tests
    }

    private static (DiarizDbContext db, WebhookSubscription sub, WebhookDelivery del) Seed(string body = "{\"a\":1}")
    {
        var db = TestDb.Create();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Name = "s", Url = "https://sink.example.com/hook",
            SecretEncrypted = "shh", EventTypes = "recording.transcribed", IsActive = true,
        };
        var del = new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_1", EventType = "recording.transcribed",
            PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        db.Webhooks.Add(sub); db.WebhookDeliveries.Add(del); db.SaveChanges();
        return (db, sub, del);
    }

    private static WebhookDeliveryProcessor Processor() =>
        new(new PlainProtector(), Options.Create(new WebhookOptions()), NullLogger<WebhookDeliveryProcessor>.Instance);

    private static HttpClient Client(StubHandler h) => new(h);

    [Fact]
    public async Task Success_marks_delivered_signs_and_resets_failures()
    {
        var (db, sub, del) = Seed();
        var h = new StubHandler(HttpStatusCode.OK);
        await Processor().ProcessDueAsync(db, Client(h), DateTimeOffset.UtcNow, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Delivered, d.Status);
        Assert.Equal(200, d.ResponseStatus);
        Assert.Equal("evt_1", h.Last!.Headers.GetValues("webhook-id").Single());
        Assert.True(h.Last.Headers.Contains("webhook-timestamp"));
        var sig = h.Last.Headers.GetValues("webhook-signature").Single();
        var ts = long.Parse(h.Last.Headers.GetValues("webhook-timestamp").Single());
        Assert.Equal(WebhookSigner.Sign("shh", "evt_1", ts, "{\"a\":1}"), sig);
        Assert.Equal(0, (await db.Webhooks.SingleAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task Failure_schedules_a_retry_and_increments_attempts()
    {
        var (db, _, _) = Seed();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.InternalServerError)),
            DateTimeOffset.UtcNow, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        Assert.Equal(1, d.AttemptCount);
        Assert.True(d.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal(500, d.ResponseStatus);
    }

    [Fact]
    public async Task Final_failure_marks_failed()
    {
        var (db, _, del) = Seed();
        del.AttemptCount = WebhookBackoff.MaxAttempts - 1; // this attempt is the last
        await db.SaveChangesAsync();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.InternalServerError)),
            DateTimeOffset.UtcNow, default);

        Assert.Equal(WebhookDeliveryStatus.Failed, (await db.WebhookDeliveries.SingleAsync()).Status);
        Assert.Equal(1, (await db.Webhooks.SingleAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task Auto_disables_after_threshold_consecutive_failures()
    {
        var (db, sub, del) = Seed();
        sub.ConsecutiveFailures = new WebhookOptions().AutoDisableThreshold - 1;
        del.AttemptCount = WebhookBackoff.MaxAttempts - 1;
        await db.SaveChangesAsync();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.BadGateway)),
            DateTimeOffset.UtcNow, default);

        var s = await db.Webhooks.SingleAsync();
        Assert.False(s.IsActive);
        Assert.NotNull(s.DisabledReason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookDeliveryProcessorTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Add WebhookOptions**

In `src/Diariz.Api/Configuration/AppOptions.cs`:

```csharp
public class WebhookOptions
{
    public const string Section = "Webhooks";
    /// <summary>Consecutive failed deliveries before a subscription is auto-disabled.</summary>
    public int AutoDisableThreshold { get; set; } = 15;
    /// <summary>Max due deliveries processed per worker tick.</summary>
    public int BatchSize { get; set; } = 20;
}
```

- [ ] **Step 4: Implement the processor**

`src/Diariz.Api/Services/WebhookDeliveryProcessor.cs`:

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Delivers due <see cref="WebhookDelivery"/> rows: signs + POSTs the stored body, then records the
/// outcome, schedules a retry with backoff on failure, and auto-disables a subscription after the failure
/// threshold. Takes `now` explicitly for deterministic tests.</summary>
public sealed class WebhookDeliveryProcessor
{
    private readonly IWebhookSecretProtector _protector;
    private readonly WebhookOptions _opts;
    private readonly ILogger<WebhookDeliveryProcessor> _log;

    public WebhookDeliveryProcessor(
        IWebhookSecretProtector protector, IOptions<WebhookOptions> opts, ILogger<WebhookDeliveryProcessor> log)
    { _protector = protector; _opts = opts.Value; _log = log; }

    public async Task ProcessDueAsync(DiarizDbContext db, HttpClient http, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        foreach (var d in due)
        {
            var sub = await db.Webhooks.FirstOrDefaultAsync(s => s.Id == d.SubscriptionId, ct);
            if (sub is null) { d.Status = WebhookDeliveryStatus.Failed; continue; } // orphan; cascade should prevent

            d.AttemptCount++;
            int? responseStatus = null;
            string? error = null;
            try
            {
                var secret = _protector.Unprotect(sub.SecretEncrypted) ?? "";
                var ts = now.ToUnixTimeSeconds();
                using var req = new HttpRequestMessage(HttpMethod.Post, sub.Url)
                {
                    Content = new StringContent(d.PayloadJson, System.Text.Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("webhook-id", d.EventId);
                req.Headers.TryAddWithoutValidation("webhook-timestamp", ts.ToString());
                req.Headers.TryAddWithoutValidation("webhook-signature", WebhookSigner.Sign(secret, d.EventId, ts, d.PayloadJson));
                using var resp = await http.SendAsync(req, ct);
                responseStatus = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode) error = $"HTTP {responseStatus}";
            }
            catch (Exception ex) { error = ex.Message; }

            d.ResponseStatus = responseStatus;
            d.LastError = error;
            sub.LastDeliveryAt = now;
            sub.LastStatus = error ?? "Delivered";

            if (error is null)
            {
                d.Status = WebhookDeliveryStatus.Delivered;
                sub.ConsecutiveFailures = 0;
            }
            else if (d.AttemptCount >= WebhookBackoff.MaxAttempts)
            {
                d.Status = WebhookDeliveryStatus.Failed;
                sub.ConsecutiveFailures++;
                if (sub.ConsecutiveFailures >= _opts.AutoDisableThreshold)
                {
                    sub.IsActive = false;
                    sub.DisabledReason = $"Auto-disabled after {sub.ConsecutiveFailures} consecutive failures.";
                }
            }
            else
            {
                d.NextAttemptAt = now.Add(WebhookBackoff.NextDelay(d.AttemptCount));
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookDeliveryProcessorTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Implement the worker** (poll loop, mirrors `AudioRetentionWorker`/`SummarizationWorker` structure - per-tick scope, resilient)

`src/Diariz.Api/Services/WebhookDeliveryWorker.cs`:

```csharp
using Diariz.Domain;

namespace Diariz.Api.Services;

/// <summary>Polls the webhook delivery table and dispatches due deliveries. Postgres-backed (not Redis) so that
/// scheduled retries and a durable delivery history come for free.</summary>
public sealed class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebhookDeliveryWorker> _log;

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopes, IHttpClientFactory http, ILogger<WebhookDeliveryWorker> log)
    { _scopes = scopes; _http = http; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>();
                var client = _http.CreateClient("webhooks");
                await processor.ProcessDueAsync(db, client, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) { _log.LogError(ex, "Webhook delivery tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }
}
```

- [ ] **Step 7: Register options, HttpClient, processor, worker**

In `src/Diariz.Api/Program.cs`:

```csharp
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));
builder.Services.AddScoped<WebhookDeliveryProcessor>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<WebhookDeliveryWorker>();
```

- [ ] **Step 8: Verify + commit**

Run: `dotnet build Diariz.slnx` (clean) and re-run the processor tests.

```bash
git add src/Diariz.Api/Services/WebhookDeliveryProcessor.cs src/Diariz.Api/Services/WebhookDeliveryWorker.cs src/Diariz.Api/Configuration/AppOptions.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/WebhookDeliveryProcessorTests.cs
git commit -m "feat: webhook delivery worker with signing, retry, and auto-disable"
```

---

## Task 5b (integration): end-to-end delivery over real HTTP + real Postgres

**Files:**
- Test: `tests/Diariz.Api.IntegrationTests/WebhookDeliveryIntegrationTests.cs`

This proves the processor + real Npgsql + a real HTTP round-trip compose (signature verifies at the receiver).

- [ ] **Step 1: Write the test** - stand up a tiny in-process HTTP listener as the webhook sink, insert a subscription + a due delivery, run `ProcessDueAsync` against `fx.CreateDbContext()` with a real `HttpClient`, and assert the sink received the body and a valid `webhook-signature`, and the delivery row flipped to `Delivered`.

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WebhookDeliveryIntegrationTests(ContainersFixture fx)
{
    private sealed class PlainProtector : IWebhookSecretProtector
    { public string? Protect(string? p) => p; public string? Unprotect(string? c) => c; }

    [Fact]
    public async Task Delivers_signed_payload_to_a_real_endpoint()
    {
        // A tiny local sink on a loopback port. The SSRF guard is on the CONTROLLER create path, not the
        // processor, so delivering to 127.0.0.1 here is fine and intentional.
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? receivedBody = null, receivedSig = null, receivedTs = null;
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            receivedBody = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            receivedSig = ctx.Request.Headers["webhook-signature"];
            receivedTs = ctx.Request.Headers["webhook-timestamp"];
            ctx.Response.StatusCode = 200; ctx.Response.Close();
        });

        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "sink", Url = $"http://127.0.0.1:{port}/hook",
            SecretEncrypted = "topsecret", EventTypes = "recording.transcribed", IsActive = true,
        };
        db.Webhooks.Add(sub);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_int", EventType = "recording.transcribed",
            PayloadJson = "{\"id\":\"evt_int\"}", NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var processor = new WebhookDeliveryProcessor(new PlainProtector(),
            Options.Create(new WebhookOptions()), NullLogger<WebhookDeliveryProcessor>.Instance);
        using var http = new HttpClient();
        await processor.ProcessDueAsync(db, http, DateTimeOffset.UtcNow, default);
        await serve;
        listener.Stop();

        Assert.Equal("{\"id\":\"evt_int\"}", receivedBody);
        var expected = "v1," + Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes("topsecret"))
            .ComputeHash(Encoding.UTF8.GetBytes($"evt_int.{receivedTs}.{{\"id\":\"evt_int\"}}")));
        Assert.Equal(expected, receivedSig);
        Assert.Equal(WebhookDeliveryStatus.Delivered, (await db.WebhookDeliveries.SingleAsync()).Status);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
```

- [ ] **Step 2: Run it** - `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~WebhookDeliveryIntegrationTests"` (Docker). Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/WebhookDeliveryIntegrationTests.cs
git commit -m "test: end-to-end webhook delivery with signature verification"
```

---

## Task 6: Emit recording.* events at the notify call-sites

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (recording.created), `src/Diariz.Api/Controllers/WorkerCallbackController.cs` (transcribed / failed)
- Test: `tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs`

**Consumes:** `IWebhookPublisher` (Task 4), `WebhookEventTypes` + `WebhookPayload` (Task 2), `IOptions<AppPublicOptions>` for links.

**Data shape** (thin): `recording.created` -> `{ recordingId, name, source, status, links }`; `recording.transcribed` -> `{ recordingId, name, status, durationMs, links }`; `recording.transcription_failed` -> `{ recordingId, name, status, error, links }`. `name` = `rec.Name ?? rec.Title`. `links` from `WebhookPayload.For(publicUrl, recordingId)` where `publicUrl` = `AppPublicOptions.PublicUrl` (fall back to `$"{Request.Scheme}://{Request.Host}"` only where a request exists - i.e. RecordingsController; WorkerCallbackController is worker-facing but still has an HttpContext, so the same fallback is fine there).

- [ ] **Step 1: Write the failing test** (inject a capturing `IWebhookPublisher` fake, assert the controller publishes the right event)

Add a fake to `tests/Diariz.Api.TestSupport/Fakes.cs`:

```csharp
public sealed class CapturingWebhookPublisher : IWebhookPublisher
{
    public readonly List<(string EventType, Guid Owner, object Data)> Published = new();
    public Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default)
    { Published.Add((eventType, ownerUserId, data)); return Task.CompletedTask; }
}
```

`tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs` - construct `WorkerCallbackController` with the capturing publisher, drive the `result` callback for a seeded transcription, and assert a `recording.transcribed` publish for the recording's owner; drive `failure` and assert `recording.transcription_failed`. Use the existing `WorkerCallbackController` test setup as a template (it exists under `tests/Diariz.Api.Tests`).

```csharp
[Fact]
public async Task Transcription_complete_publishes_recording_transcribed()
{
    // arrange: seed a Recording (owner O) + Transcription; build WorkerCallbackController with the worker secret,
    // FakeHubContext, and a CapturingWebhookPublisher; call Result(...) with the matching X-Worker-Secret header.
    // assert:
    Assert.Contains(publisher.Published, p =>
        p.EventType == WebhookEventTypes.RecordingTranscribed && p.Owner == ownerId);
}
```

> Follow the existing `WorkerCallbackController` unit test for how it seeds the transcription + sets the `X-Worker-Secret` header via `Http.Context(headers: ...)`. Add the `recording.created` assertion in a `RecordingsController` test only if that controller already has a unit-test harness that exercises `Upload` with a `FakeAudioStorage` + `FakeJobQueue`; if `Upload` is only covered by integration tests, assert `recording.created` in an integration test instead (construct the controller with a `CapturingWebhookPublisher`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingWebhookEmitTests"`
Expected: FAIL (no publish happens yet).

- [ ] **Step 3: Emit from WorkerCallbackController**

Inject `IWebhookPublisher _webhooks` and `IOptions<AppPublicOptions> _appOpts` into the controller ctor. After the **complete** site (`WorkerCallbackController.cs:135-136`, right after `NotifyStatusAsync`):

```csharp
        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        var rec = transcription.Recording;
        if (rec.Status == RecordingStatus.Transcribed || rec.Status == RecordingStatus.Summarizing)
            await _webhooks.PublishAsync(WebhookEventTypes.RecordingTranscribed, rec.UserId, new
            {
                recordingId = rec.Id, name = rec.Name ?? rec.Title, status = rec.Status.ToString(),
                durationMs = body.DurationMs, links = WebhookPayload.For(publicUrl, rec.Id),
            });
```

After the **failure** site (`:153-154`):

```csharp
        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        var rec = transcription.Recording;
        await _webhooks.PublishAsync(WebhookEventTypes.RecordingTranscriptionFailed, rec.UserId, new
        {
            recordingId = rec.Id, name = rec.Name ?? rec.Title, status = RecordingStatus.Failed.ToString(),
            error = body.Error, links = WebhookPayload.For(publicUrl, rec.Id),
        });
```

Add `using Diariz.Api.Webhooks;` and `using Diariz.Api.Services;`.

- [ ] **Step 4: Emit recording.created from RecordingsController**

Inject `IWebhookPublisher _webhooks` (and reuse the existing `_appOpts` if present, else add `IOptions<AppPublicOptions>`). After `await _db.SaveChangesAsync();` in `Upload` (`:278`):

```csharp
        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        await _webhooks.PublishAsync(WebhookEventTypes.RecordingCreated, UserId, new
        {
            recordingId = rec.Id, name = rec.Name ?? rec.Title, source = rec.Source.ToString(),
            status = rec.Status.ToString(), links = WebhookPayload.For(publicUrl, rec.Id),
        });
```

- [ ] **Step 5: Update the other construction sites**

`git grep -n "new WorkerCallbackController\|new RecordingsController" tests src` - update every construction (prod DI is automatic; test sites must pass the new ctor args - a `CapturingWebhookPublisher` and `Options.Create(new AppPublicOptions())`). This is the cross-cutting ctor-change sweep.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingWebhookEmitTests"` then `dotnet build Diariz.slnx`.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Controllers/RecordingsController.cs src/Diariz.Api/Controllers/WorkerCallbackController.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs
git commit -m "feat: emit recording.created/transcribed/transcription_failed webhooks"
```

---

## Task 7: Emit formula_result.* events

**Files:**
- Modify: `src/Diariz.Api/Services/FormulaRunProcessor.cs`, `src/Diariz.Api/Services/FormulaRunWorker.cs`
- Test: `tests/Diariz.Api.Tests/FormulaWebhookEmitTests.cs`

**Consumes:** `IWebhookPublisher`, `WebhookEventTypes`, `WebhookPayload`.

**Design note:** `FormulaRunProcessor.ProcessAsync` is a **static** method. Add two parameters - `IWebhookPublisher webhooks, string publicUrl` - and pass them from `FormulaRunWorker` (which resolves `IWebhookPublisher` from its per-job scope and reads `AppPublicOptions.PublicUrl` from `IOptions`). The worker has no request context, so `publicUrl` MUST come from configuration (empty -> pass empty; links then omit-or-relative; require `App:PublicUrl` in deployments that enable webhooks, per Global Constraints).

**Data shape:** `formula_result.completed` / `.failed` -> `{ recordingId, sectionId, formulaId, formulaResultId, status, links.result }` where `links.result` = `{publicUrl}/api/recordings/{recordingId}/formula-results/{formulaResultId}` (the existing result-fetch endpoint). `signals` is omitted in Phase 2 (Phase 3 adds it).

- [ ] **Step 1: Write the failing test** - call `FormulaRunProcessor.ProcessAsync(...)` with a `CapturingWebhookPublisher` for a job whose run succeeds, assert a `formula_result.completed` publish with the right `formulaResultId`; and a failing run publishes `formula_result.failed`. Model the arrange on the existing `FormulaRunProcessor`/`RunFormulaTool` tests. Key assertion:

```csharp
Assert.Contains(publisher.Published, p =>
    p.EventType == WebhookEventTypes.FormulaResultCompleted);
```

- [ ] **Step 2: Run test to verify it fails** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaWebhookEmitTests"`. Expected: FAIL to compile (ProcessAsync signature) / no publish.

- [ ] **Step 3: Thread the publisher through** - add `IWebhookPublisher webhooks, string publicUrl` to `FormulaRunProcessor.ProcessAsync(...)` (append them). At the **Ready** site (`:71-72`, right after `NotifyFormulaStatusAsync`):

```csharp
            await webhooks.PublishAsync(WebhookEventTypes.FormulaResultCompleted, job.UserId, new
            {
                recordingId = job.RecordingId, sectionId = job.SectionId, formulaId = job.FormulaId,
                formulaResultId = job.ResultId, status = nameof(FormulaRunStatus.Ready),
                links = new { result = FormulaResultLink(publicUrl, job.RecordingId, job.ResultId) },
            });
```

At the **Failed** site (`:113-114`):

```csharp
        await webhooks.PublishAsync(WebhookEventTypes.FormulaResultFailed, job.UserId, new
        {
            recordingId = job.RecordingId, sectionId = job.SectionId, formulaId = job.FormulaId,
            formulaResultId = job.ResultId, status = nameof(FormulaRunStatus.Failed),
            links = new { result = FormulaResultLink(publicUrl, job.RecordingId, job.ResultId) },
        });
```

Add a small private helper in the processor (guards a null recordingId - a formula run can be section-scoped):

```csharp
    private static string? FormulaResultLink(string publicUrl, Guid? recordingId, Guid resultId) =>
        recordingId is { } rid && !string.IsNullOrWhiteSpace(publicUrl)
            ? $"{publicUrl.TrimEnd('/')}/api/recordings/{rid}/formula-results/{resultId}"
            : null;
```

- [ ] **Step 4: Pass from the worker** - in `FormulaRunWorker`, resolve `IWebhookPublisher` from the job scope and read `AppPublicOptions.PublicUrl` (inject `IOptions<AppPublicOptions>` into the worker), then pass both into `ProcessAsync(...)`. Update any OTHER caller of `ProcessAsync` (`git grep -n "FormulaRunProcessor.ProcessAsync"`) to pass the two new args - tests can pass a `CapturingWebhookPublisher` + `""`.

- [ ] **Step 5: Run tests + build** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaWebhookEmitTests"` then `dotnet build Diariz.slnx`. Expected: PASS + clean.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/FormulaRunProcessor.cs src/Diariz.Api/Services/FormulaRunWorker.cs tests/Diariz.Api.Tests/FormulaWebhookEmitTests.cs
git commit -m "feat: emit formula_result.completed/failed webhooks"
```

---

## Task 8: WebhooksController (CRUD + test + deliveries, gated + SSRF + secret-once)

**Files:**
- Create: `src/Diariz.Api/Controllers/WebhooksController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (webhook DTOs)
- Test: `tests/Diariz.Api.Tests/WebhooksControllerTests.cs`

**DTOs:**

```csharp
public record WebhookSubscriptionDto(
    Guid Id, string Name, string Url, string[] EventTypes, bool IsActive, int ConsecutiveFailures,
    string? DisabledReason, DateTimeOffset? LastDeliveryAt, string? LastStatus, DateTimeOffset CreatedAt);
public record WebhookCreatedDto(Guid Id, string Name, string Url, string[] EventTypes, string Secret);
public record CreateWebhookRequest(string? Name, string Url, string[] EventTypes);
public record UpdateWebhookRequest(string? Name, string Url, string[] EventTypes, bool IsActive);
public record WebhookDeliveryDto(
    Guid Id, string EventType, string Status, int AttemptCount, int? ResponseStatus, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt);
```

**Behaviour:** all actions gated on `WebhooksEnabled` (else 403); owner-scoped; unknown event types rejected (only `WebhookEventTypes.Subscribable` allowed); URL SSRF-validated on create/update; secret generated (`dz_whsec_` + base64url(32 bytes)), encrypted, returned once on create; per-user cap (20); update with `IsActive` true when currently auto-disabled resets `ConsecutiveFailures`/`DisabledReason`; `POST {id}/test` inserts a `webhook.ping` delivery (Pending, now); `GET {id}/deliveries` returns the last ~50.

- [ ] **Step 1: Write the failing tests**

`tests/Diariz.Api.Tests/WebhooksControllerTests.cs` (uses `TestDb`, `Http.Context`, `FixedPlatformSettings`, a pass-through `IWebhookSecretProtector`, and a stub `IWebhookUrlValidator` returning Valid):

```csharp
[Fact]
public async Task Create_when_disabled_is_forbidden()
{
    var db = TestDb.Create();
    db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
    await db.SaveChangesAsync();
    var c = Controller(db, Guid.NewGuid(), urlOk: true);
    var res = await c.Create(new CreateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }));
    Assert.IsType<ForbidResult>(res.Result);
}

[Fact]
public async Task Create_returns_secret_once_and_persists_encrypted()
{
    var db = Enabled(); var userId = Guid.NewGuid();
    var c = Controller(db, userId, urlOk: true);
    var res = await c.Create(new CreateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }));
    var dto = Assert.IsType<WebhookCreatedDto>(res.Value);
    Assert.StartsWith("dz_whsec_", dto.Secret);
    var row = await db.Webhooks.SingleAsync();
    Assert.NotEqual(dto.Secret, row.SecretEncrypted); // stored value is the protected form (pass-through fake keeps it equal? use a prefixing fake)
    Assert.Equal("recording.transcribed", row.EventTypes);
}

[Fact]
public async Task Create_rejects_unknown_event_type() { /* -> BadRequest */ }

[Fact]
public async Task Create_rejects_ssrf_url() { /* validator returns Invalid -> BadRequest */ }

[Fact]
public async Task Test_endpoint_enqueues_a_ping_delivery()
{
    var db = Enabled(); var userId = Guid.NewGuid();
    var sub = /* insert an owned subscription */;
    var c = Controller(db, userId, urlOk: true);
    await c.SendTest(sub.Id);
    var d = await db.WebhookDeliveries.SingleAsync();
    Assert.Equal(WebhookEventTypes.Ping, d.EventType);
}
```

> For the encrypted-at-rest assertion, use a protector fake that prefixes (e.g. `Protect(p) => "enc:" + p`) so `row.SecretEncrypted != dto.Secret` is meaningful. Fill in the `Controller(...)` helper to build `new WebhooksController(db, new FixedPlatformSettings(db), protector, validatorStub, publisher)` with `ControllerContext = Http.Context(userId)`.

- [ ] **Step 2: Run tests to verify they fail** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhooksControllerTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Implement the controller**

`src/Diariz.Api/Controllers/WebhooksController.cs`:

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/user/webhooks")]
public class WebhooksController : ControllerBase
{
    public const int MaxPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;
    private readonly IWebhookSecretProtector _protector;
    private readonly IWebhookUrlValidator _urls;

    public WebhooksController(DiarizDbContext db, IPlatformSettingsService platform,
        IWebhookSecretProtector protector, IWebhookUrlValidator urls)
    { _db = db; _platform = platform; _protector = protector; _urls = urls; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> EnabledAsync() => (await _platform.GetAsync()).WebhooksEnabled;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WebhookSubscriptionDto>>> List()
    {
        if (!await EnabledAsync()) return Forbid();
        var rows = await _db.Webhooks
            .Where(s => s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<WebhookCreatedDto>> Create(CreateWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var invalid = Validate(req.Url, req.EventTypes, out var events, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);
        if (await _db.Webhooks.CountAsync(s => s.OwnerUserId == UserId) >= MaxPerUser)
            return BadRequest("Automation limit reached. Delete one before adding another.");

        var secret = "dz_whsec_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = UserId, Scope = WebhookScope.Personal,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim(),
            Url = req.Url.Trim(), SecretEncrypted = _protector.Protect(secret)!, EventTypes = WebhookEventTypes.Join(events),
        };
        _db.Webhooks.Add(row);
        await _db.SaveChangesAsync();
        return new WebhookCreatedDto(row.Id, row.Name, row.Url, events, secret);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WebhookSubscriptionDto>> Update(Guid id, UpdateWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == UserId);
        if (row is null) return NotFound();
        var invalid = Validate(req.Url, req.EventTypes, out var events, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);

        row.Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim();
        row.Url = req.Url.Trim();
        row.EventTypes = WebhookEventTypes.Join(events);
        if (req.IsActive && !row.IsActive) { row.ConsecutiveFailures = 0; row.DisabledReason = null; }
        row.IsActive = req.IsActive;
        await _db.SaveChangesAsync();
        return ToDto(row);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == UserId);
        if (row is null) return NotFound();
        _db.Webhooks.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> SendTest(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == UserId);
        if (row is null) return NotFound();
        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = row.Id, EventId = eventId, EventType = WebhookEventTypes.Ping,
            PayloadJson = WebhookPayload.Build(eventId, WebhookEventTypes.Ping, DateTimeOffset.UtcNow,
                new { message = "This is a test event from Diariz." }),
            Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDto>>> Deliveries(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        if (!await _db.Webhooks.AnyAsync(s => s.Id == id && s.OwnerUserId == UserId)) return NotFound();
        var rows = await _db.WebhookDeliveries.Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync();
        return Ok(rows.Select(d => new WebhookDeliveryDto(
            d.Id, d.EventType, d.Status.ToString(), d.AttemptCount, d.ResponseStatus, d.LastError,
            d.CreatedAt, d.NextAttemptAt)).ToList());
    }

    private static string? Validate(string url, string[] types, out string[] events, out string? reason)
    {
        events = (types ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(url)) { reason = "A destination URL is required."; return reason; }
        if (events.Length == 0) { reason = "Choose at least one event."; return reason; }
        if (events.Any(t => !WebhookEventTypes.Subscribable.Contains(t)))
        { reason = "Unknown event type."; return reason; }
        reason = null; return null;
    }

    private static WebhookSubscriptionDto ToDto(WebhookSubscription s) => new(
        s.Id, s.Name, s.Url, WebhookEventTypes.Split(s.EventTypes), s.IsActive, s.ConsecutiveFailures,
        s.DisabledReason, s.LastDeliveryAt, s.LastStatus, s.CreatedAt);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 4: Run tests + build** - fill in the test helpers, run `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhooksControllerTests"`, then `dotnet build Diariz.slnx`.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/WebhooksController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/WebhooksControllerTests.cs
git commit -m "feat: webhooks management API (CRUD, test, deliveries)"
```

---

## Task 9: Expose webhooksEnabled on the user profile

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (UserProfileDto), `src/Diariz.Api/Controllers/UserProfileController.cs`
- Test: extend `tests/Diariz.Api.Tests/UserProfileControllerTests.cs` (or create if absent)

**Why:** a non-admin SPA cannot read `/api/platform/settings`; it learns feature flags from `/api/user/profile`. The Automations tab visibility needs `webhooksEnabled` there, exactly as `apiAccessEnabled` is exposed today.

- [ ] **Step 1: Write the failing test** - seed `PlatformSettings.WebhooksEnabled = true`, call `UserProfileController.Get()`, assert `dto.WebhooksEnabled` is true.

```csharp
[Fact]
public async Task Profile_reports_webhooks_enabled()
{
    var db = TestDb.Create();
    db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = true });
    // seed the user + settings as the existing profile tests do
    var c = /* build UserProfileController for that user */;
    var res = await c.Get();
    Assert.True(res.Value!.WebhooksEnabled);
}
```

- [ ] **Step 2: Run to verify it fails** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserProfileControllerTests"`. Expected: FAIL to compile (`WebhooksEnabled` absent).

- [ ] **Step 3: Add the field + projection** - in `ApiDtos.cs`, append to `UserProfileDto`:

```csharp
    bool WebhooksEnabled = false,
```

Insert it **before** `PermissionsDto? Permissions = null` to keep `Permissions` last (optional trailing param). In `UserProfileController.Get()`, read it alongside `apiAccessEnabled`:

```csharp
        var settings = await _platform.GetAsync();
        var apiAccessEnabled = settings.ApiAccessEnabled;
        var webhooksEnabled = settings.WebhooksEnabled;
```

and pass `WebhooksEnabled: webhooksEnabled,` in the `UserProfileDto` construction (right after `ApiAccessEnabled: apiAccessEnabled,`).

- [ ] **Step 4: Run tests + build** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserProfileControllerTests"`, then `dotnet build Diariz.slnx`.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserProfileController.cs tests/Diariz.Api.Tests/UserProfileControllerTests.cs
git commit -m "feat: expose webhooksEnabled on the user profile"
```

---

## Task 10: Web plumbing - types + api client + profile flag

**Files:**
- Modify: `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`
- Test: `apps/web/src/lib/api.test.ts` (if the repo has api-level tests; else assert via the component test in Task 11)

**Interfaces produced (TS):**

```ts
export interface WebhookSubscription {
  id: string; name: string; url: string; eventTypes: string[]; isActive: boolean;
  consecutiveFailures: number; disabledReason: string | null;
  lastDeliveryAt: string | null; lastStatus: string | null; createdAt: string;
}
export interface WebhookCreated {
  id: string; name: string; url: string; eventTypes: string[]; secret: string;
}
export interface WebhookDelivery {
  id: string; eventType: string; status: string; attemptCount: number;
  responseStatus: number | null; lastError: string | null; createdAt: string; nextAttemptAt: string | null;
}
export interface CreateWebhookBody { name: string; url: string; eventTypes: string[]; }
export interface UpdateWebhookBody extends CreateWebhookBody { isActive: boolean; }
```

- [ ] **Step 1: Add `webhooksEnabled` to `UserProfile`** in `types.ts`:

```ts
  webhooksEnabled: boolean;   // drives the "Automations" tab
```

- [ ] **Step 2: Add the api client methods** in `apps/web/src/lib/api.ts` (mirror the existing `const { data } = await http.<verb>(...)` shape):

```ts
async listWebhooks(): Promise<WebhookSubscription[]> {
  const { data } = await http.get<WebhookSubscription[]>("/api/user/webhooks");
  return data;
},
async createWebhook(body: CreateWebhookBody): Promise<WebhookCreated> {
  const { data } = await http.post<WebhookCreated>("/api/user/webhooks", body);
  return data;
},
async updateWebhook(id: string, body: UpdateWebhookBody): Promise<WebhookSubscription> {
  const { data } = await http.put<WebhookSubscription>(`/api/user/webhooks/${id}`, body);
  return data;
},
async deleteWebhook(id: string): Promise<void> {
  await http.delete(`/api/user/webhooks/${id}`);
},
async testWebhook(id: string): Promise<void> {
  await http.post(`/api/user/webhooks/${id}/test`);
},
async listWebhookDeliveries(id: string): Promise<WebhookDelivery[]> {
  const { data } = await http.get<WebhookDelivery[]>(`/api/user/webhooks/${id}/deliveries`);
  return data;
},
```

Import the new types at the top of `api.ts`.

- [ ] **Step 3: Verify** - from `apps/web`: `npm run build` (tsc typecheck) clean.

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts
git commit -m "feat: web types and api client for webhooks"
```

---

## Task 11: AutomationsSection - guided create flow + tab + i18n

**Files:**
- Create: `apps/web/src/components/AutomationsSection.tsx`
- Modify: `apps/web/src/components/PreferencesModal.tsx`, `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/AutomationsSection.test.tsx`, extend `PreferencesModal.test.tsx`

**Scope of this task:** the tab wiring (gated on `profile.webhooksEnabled`), and the **create** flow (pick events -> destination URL with provider hints -> save), plus i18n. The list/status/test/delete UI is Task 12.

- [ ] **Step 1: Write failing tests**
  - `PreferencesModal.test.tsx`: when `getProfile` resolves `{ webhooksEnabled: true, ... }`, an "Automations" tab renders; when false, it does not. (Mirror the existing `apiAccessEnabled` -> Developers-tab test.)
  - `AutomationsSection.test.tsx`: tick the "A recording finishes transcribing" event, type a URL, click Create, assert `api.createWebhook` was called with `{ eventTypes: ["recording.transcribed"], url: "...", name: ... }`.

```tsx
it("creates an automation with the chosen event and url", async () => {
  const createWebhook = vi.mocked(api.createWebhook).mockResolvedValue({
    id: "1", name: "n", url: "https://x/y", eventTypes: ["recording.transcribed"], secret: "dz_whsec_x",
  });
  render(<Wrapped />);
  fireEvent.click(await screen.findByLabelText(/finishes transcribing/i));
  fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
  fireEvent.click(screen.getByRole("button", { name: /create|save/i }));
  await waitFor(() => expect(createWebhook).toHaveBeenCalledWith(
    expect.objectContaining({ url: "https://x/y", eventTypes: ["recording.transcribed"] })));
});
```

- [ ] **Step 2: Run to verify they fail** - from `apps/web`: `npm test -- AutomationsSection PreferencesModal`. Expected: FAIL.

- [ ] **Step 3: Add the tab to PreferencesModal** - add `"automations"` to the `PreferencesTab` union; add `...(profile?.webhooksEnabled ? [{ id: "automations" as const, label: t("tabAutomations") }] : [])` to the `tabs` array; add `{tab === "automations" && <AutomationsSection />}` to the render.

- [ ] **Step 4: Build AutomationsSection** (create flow). Key logic (use `useTranslation("account")`, `@tanstack/react-query`, plain-language event checkboxes mapped to the five keys, a URL field labelled by `t("automationDestinationUrl")` matching `/destination url/i`, and provider hint tabs). Event options:

```tsx
const EVENTS = [
  { key: "recording.created", label: t("evtRecordingCreated") },
  { key: "recording.transcribed", label: t("evtRecordingTranscribed") },
  { key: "recording.transcription_failed", label: t("evtRecordingFailed") },
  { key: "formula_result.completed", label: t("evtFormulaCompleted") },
  { key: "formula_result.failed", label: t("evtFormulaFailed") },
];
```

On Create: `await api.createWebhook({ name: name || t("automationDefaultName"), url, eventTypes: selected })`, then show the returned `secret` once in an "advanced/verify" disclosure with a copy button, and invalidate the `["webhooks"]` query. On SSRF/validation error, surface `apiErrorMessage(e, ...)`.

- [ ] **Step 5: i18n (all four locales)** - add to `en/account.json` (translate for de/es/fr with correct native spelling, no fancy dashes):

```json
  "tabAutomations": "Automations",
  "automationsTitle": "Automations",
  "automationsIntro": "Send your meeting events to tools like Zapier, n8n, or Make. Pick what should trigger, paste the URL from your tool, and test it.",
  "automationDefaultName": "Automation",
  "automationDestinationUrl": "Destination URL",
  "automationEventsHeading": "What should trigger it?",
  "evtRecordingCreated": "A recording is created",
  "evtRecordingTranscribed": "A recording finishes transcribing",
  "evtRecordingFailed": "A recording fails to transcribe",
  "evtFormulaCompleted": "A formula finishes",
  "evtFormulaFailed": "A formula fails",
  "automationCreate": "Create automation",
  "automationSecretOnce": "Signing secret (shown once) - optional, use it to verify requests really came from Diariz.",
  "automationCreateError": "Could not create the automation.",
  "automationHintZapier": "In Zapier: add a 'Webhooks by Zapier - Catch Hook' trigger and paste its Custom Webhook URL here.",
  "automationHintN8n": "In n8n: add a Webhook node and paste its Production URL here.",
  "automationHintOther": "Paste the webhook URL your tool gives you."
```

- [ ] **Step 6: Run tests + build** - from `apps/web`: `npm test -- AutomationsSection PreferencesModal` and `npm run build`. Expected: PASS + clean.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/AutomationsSection.tsx apps/web/src/components/PreferencesModal.tsx apps/web/src/components/AutomationsSection.test.tsx apps/web/src/components/PreferencesModal.test.tsx apps/web/src/locales
git commit -m "feat: Automations tab and guided create flow"
```

---

## Task 12: AutomationsSection - list, status, test, delete, inline token offer

**Files:**
- Modify: `apps/web/src/components/AutomationsSection.tsx`, `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: extend `apps/web/src/components/AutomationsSection.test.tsx`

- [ ] **Step 1: Write failing tests**
  - The list renders each automation as a card with its trigger chips and a status pill; an auto-disabled one (`isActive: false`, `disabledReason` set) shows a "Paused" state with a Re-enable action.
  - Clicking "Send test event" calls `api.testWebhook(id)`.
  - Clicking delete calls `api.deleteWebhook(id)`.
  - When a formula event is selected in the create flow, an inline "create a read-only token" offer appears; clicking it calls `api.createApiToken(expect.any(String), { readOnly: true, expiresAt: null })`.

```tsx
it("sends a test event", async () => {
  vi.mocked(api.listWebhooks).mockResolvedValue([{ id: "1", name: "n", url: "https://x/y",
    eventTypes: ["recording.transcribed"], isActive: true, consecutiveFailures: 0, disabledReason: null,
    lastDeliveryAt: null, lastStatus: null, createdAt: new Date().toISOString() }]);
  const testWebhook = vi.mocked(api.testWebhook).mockResolvedValue();
  render(<Wrapped />);
  fireEvent.click(await screen.findByRole("button", { name: /send test/i }));
  await waitFor(() => expect(testWebhook).toHaveBeenCalledWith("1"));
});
```

- [ ] **Step 2: Run to verify they fail** - `npm test -- AutomationsSection`. Expected: FAIL.

- [ ] **Step 3: Implement** the list (`useQuery(["webhooks"], api.listWebhooks)`), status pill (Active/Paused from `isActive`+`consecutiveFailures`+`lastStatus`), per-card "Send test event" (`api.testWebhook`), delete (`api.deleteWebhook` + invalidate), Re-enable (`api.updateWebhook` with `isActive: true`), and the inline token offer shown when any selected event starts with `formula_result` (calls `api.createApiToken(t("automationTokenName"), { readOnly: true, expiresAt: null })`, shows the token once).

- [ ] **Step 4: i18n** - add the new keys in all four locales (no fancy dashes):

```json
  "automationSendTest": "Send test event",
  "automationDelete": "Delete",
  "automationReenable": "Re-enable",
  "automationActive": "Active",
  "automationPaused": "Paused - check the URL",
  "automationLastDelivered": "delivered {{when}}",
  "automationTokenOffer": "This automation may need to read results back into your workflow. Create a read-only access token?",
  "automationTokenCreate": "Create token",
  "automationTokenName": "Automation token",
  "automationEmpty": "No automations yet."
```

- [ ] **Step 5: Run tests + build** - `npm test -- AutomationsSection` and `npm run build`. Expected: PASS + clean.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/AutomationsSection.tsx apps/web/src/components/AutomationsSection.test.tsx apps/web/src/locales
git commit -m "feat: automation list, status, test-event, delete, inline token offer"
```

---

## Task 13: Release, docs, verification

**Files:** `version.json` + 3 mirrors; `apps/web/src/lib/releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`; `docs/Data_Schema.md`.

- [ ] **Step 1: Bump to `0.153.0`** in `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`. (Main is at `0.152.0` after PR #334; confirm before bumping.)

- [ ] **Step 2: Prepend `RELEASES[0]`** (version must equal `version.json`; `pr: 0` until opened):

```ts
  {
    version: "0.153.0",
    date: "2026-07-24",
    pr: 0, // set on open
    headline: "Automations: send your meeting events to Zapier, n8n, and more",
    summary:
      "You can now create Automations that send a signed webhook to your tools when a recording is created, " +
      "finishes (or fails) transcription, or a formula finishes (or fails). Pick the triggers, paste your tool's " +
      "webhook URL, and send a test event. Deliveries are retried automatically and a failing endpoint is paused. " +
      "Requires a platform admin to enable Automations.",
    added: [
      "An Automations tab in Preferences to register outbound webhooks (personal, gated on the platform toggle)",
      "Standards-compliant signed delivery (HMAC-SHA256), automatic retries with backoff, and auto-pause on repeated failure",
      "A 'Send test event' button and a recent-deliveries view per automation",
    ],
  },
```

- [ ] **Step 3: CAPABILITIES** - add/adjust an "Automations" row in the About-box table in `releases.ts` (concise, no fancy dashes).

- [ ] **Step 4: Docs in lockstep**
  - `README.md` Features table: add an Automations/outbound-webhooks row.
  - `docs/features.md`: matching prose bullet.
  - `docs/Overall_Synopsis_of_Platform.md`: the outbound-webhook contract (event catalog, Standard-Webhooks signing headers, the Postgres-backed `WebhookDelivery` queue + `WebhookDeliveryWorker`, the personal-scope matching, SSRF validation, the `App:PublicUrl` requirement, the `Webhooks` options).
  - `docs/Data_Schema.md`: the `WebhookSubscriptions` + `WebhookDeliveries` tables (columns/keys/indexes/cascades) and the `AddWebhooks` migration in the history table.

- [ ] **Step 5: Run all suites**
  - `dotnet build Diariz.slnx`
  - `dotnet test tests/Diariz.Api.Tests`
  - `dotnet test tests/Diariz.Api.IntegrationTests` (Docker)
  - from `apps/web`: `npm test` (incl. `releases.test.ts`) and `npm run build`
  Report exact pass counts.

- [ ] **Step 6: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts README.md docs
git commit -m "chore: release 0.153.0 - outbound webhooks (Automations)"
```

- [ ] **Step 7: Push + PR** (controller does this after the final whole-branch review). PR body must state: **deployment surface = server redeploy only** (no desktop); the new **`App:PublicUrl` requirement** for deployments enabling webhooks; migration additive + forward-restore-safe (no `CurrentFormat` bump); Phase 2 of 3. Then set the real PR number into `RELEASES[0].pr`.

---

## Notes for the executor

- **Ctor-change sweeps** (Tasks 6, 7): adding `IWebhookPublisher` to `WorkerCallbackController`/`RecordingsController` and two params to `FormulaRunProcessor.ProcessAsync` breaks other construction/call sites - grep and update every one (prod is DI; tests pass the `CapturingWebhookPublisher` fake). Build `Diariz.slnx` to catch them all, including the integration project.
- **Payload bytes are load-bearing:** never re-serialize `PayloadJson` between store and send - the signature is over the exact stored string. It is `text`, not `jsonb`, for this reason.
- **`App:PublicUrl`:** the delivery worker and the formula-event site have no HttpContext. Links depend on `App:PublicUrl`; document it as required when webhooks are enabled. The two controller sites (RecordingsController, WorkerCallbackController) may fall back to `Request.Host`.
- **Phase boundary:** `WebhookScope.Platform`, `SignalFilter`, `WorkflowSignal`, and inline formula output are Phase 3 - do not build them here. The `Scope` column exists now (defaults Personal) purely so Phase 3 adds behavior without a second table rewrite.
- **Minor carried from Phase 1** (optional, if trivial while nearby): the token list in `DeveloperAccessSection` still doesn't show scope/expiry - out of scope for Phase 2 unless the reviewer folds it in.
- **Per-subscription delivery rate cap (spec section 12) is deliberately deferred**, not skipped: in Phase 2 deliveries are produced only by real user activity (a recording created/transcribed, a formula run) and retries are backoff-scheduled, so no loop can hammer a target. `BatchSize` bounds per-tick throughput. A per-subscription token-bucket becomes worth adding in Phase 3 when platform-scoped subscriptions can fan out across many users' events - note it there.
