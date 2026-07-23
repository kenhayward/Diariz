# Integrations Phase 3: Workflow Signals + platform routing + inline output - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a formula author attach a plain-language named "signal" to a formula; when that formula finishes, the signal rides outward on the webhook event, and a platform admin's signal-routed subscription (wired once, for everyone) receives it - with the formula output embedded inline.

**Architecture:** A new admin-defined `WorkflowSignal` vocabulary + a `FormulaWorkflowSignal` join. `WebhookSubscription` gains a `SignalFilter` and its `Platform` scope becomes live. The publisher gains a `signals` parameter and platform-scope matching (fires across users when a subscription's signal filter intersects the event's signals); for platform, signal-routed `formula_result.*` deliveries it embeds the formula output. Admins manage signals + platform subscriptions in Settings; authors pick signals in the formula editor.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit (unit + Testcontainers integration), React 19 + TS + Vite + Tailwind v4, vitest, i18next (en/de/es/fr).

This is **Phase 3 of 3** (the final phase). Phase 1 (v0.151.0) shipped scoped tokens + platform toggles; Phase 2 (v0.153.0) shipped personal outbound webhooks. Phase 3 completes the integrations feature.

Source spec: `docs/superpowers/specs/2026-07-23-integrations-design.md` (sections 7, 8-10, 12-13). Phase 2 plan (conventions): `docs/superpowers/plans/2026-07-24-integrations-phase2-outbound-webhooks.md`.

## Global Constraints

- **TDD:** failing test first, watch it fail, then minimal code.
- **No em/en dashes in user-facing copy** (UI strings, all four locale catalogs, release notes). Plain hyphen `-`. Internal docs/code exempt.
- **i18n:** every new user-facing key is added to `apps/web/src/locales/{en,de,es,fr}/account.json` in all four languages, correct native spelling (German umlauts, Spanish/French accents), French curly apostrophes `'` to match the file, no fancy dashes.
- **Signed-body integrity:** `WebhookDelivery.PayloadJson` stays plain `text`; never re-serialize a stored body. The publisher builds each body once from the passed data object.
- **Personal deliveries stay thin.** Inline output (`data.output`/`recordingName`/`formulaName`) is embedded ONLY on a **platform**, signal-routed `formula_result.*` delivery. Personal subscriptions never embed output.
- **Platform matching rule (spec section 10):** a platform subscription matches when `Scope=Platform AND IsActive AND eventType in EventTypes AND SignalFilter intersects the event's signals`. A platform subscription with an **empty `SignalFilter` matches nothing** (guard against firing on everyone's every event). Platform subscriptions are NOT owner-scoped.
- **Personal matching rule (unchanged + narrowing):** `Scope=Personal AND IsActive AND OwnerUserId=owner AND eventType in EventTypes AND (SignalFilter empty OR intersects signals)`. Phase 2 personal subs have an empty `SignalFilter`, so they keep matching on event type alone - backward compatible.
- **Authorization:** Workflow Signal CRUD and platform-subscription CRUD require the **`ManagePlatform`** policy. Listing *active* signals (for the formula picker) is available to any authenticated user. Personal Automations are unchanged (auth + `WebhooksEnabled`).
- **Backward compatibility:** `SignalFilter` is nullable (existing personal subs have none). New tables (`WorkflowSignals`, `FormulaWorkflowSignals`) have no pre-existing rows, so migration column defaults need no hand-editing.
- **Versioning:** functional enhancement -> Minor bump `0.153.0` -> `0.154.0`, mirrored to `version.json` + `apps/web/package.json` + `apps/desktop/package.json` + `src/Diariz.Api/Diariz.Api.csproj`, `RELEASES[0]` == `version.json`. (Confirm main's version before bumping; if it moved past 0.153.0, bump to the next Minor.)
- **Deployment surface:** server redeploy only (no desktop). Migration additive + forward-restore-safe (no `CurrentFormat` bump).

---

## File Structure

**Backend - create:**
- `src/Diariz.Domain/Entities/WorkflowSignal.cs`, `FormulaWorkflowSignal.cs`.
- `src/Diariz.Domain/Migrations/<ts>_AddWorkflowSignals.cs` (generated).
- `src/Diariz.Api/Webhooks/WebhookSignals.cs` - CSV signal-filter helpers (pure).
- `src/Diariz.Api/Controllers/WorkflowSignalsController.cs` - list active (authed) + admin CRUD.
- `src/Diariz.Api/Controllers/PlatformWebhooksController.cs` - admin platform-subscription CRUD.

**Backend - modify:**
- `src/Diariz.Domain/Entities/WebhookSubscription.cs` - add `SignalFilter`.
- `src/Diariz.Domain/DiarizDbContext.cs` - two DbSets + config; `SignalFilter` maxlen.
- `src/Diariz.Api/Services/WebhookPublisher.cs` - `signals` param + platform matching + inline output.
- `src/Diariz.Api/Services/FormulaRunProcessor.cs` - load signals, emit `signals[]` + platform inline output.
- `src/Diariz.Api/Controllers/FormulasController.cs` - accept + reconcile attached signals; expose on `FormulaDto`.
- `src/Diariz.Api/Controllers/WebhooksController.cs` - expose `signalFilter` on personal subs (optional narrowing).
- `src/Diariz.Api/Contracts/ApiDtos.cs` - signal DTOs, extend webhook DTOs + `FormulaDto`.
- `tests/Diariz.Api.TestSupport/Fakes.cs` - update `CapturingWebhookPublisher` for the new signature.

**Web - modify:**
- `apps/web/src/lib/types.ts` - `WorkflowSignal`, `Formula.signals`, webhook `signalFilter`/`scope`.
- `apps/web/src/lib/api.ts` - signal API + platform-webhook API.
- `apps/web/src/components/FormulaEditModal.tsx` - signal multi-select.
- `apps/web/src/components/SettingsModal.tsx` - Workflow Signals CRUD + Platform automations sections.
- `apps/web/src/locales/{en,de,es,fr}/account.json` - strings.

**Docs / version:** `version.json` + 3 mirrors; `releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`; `docs/Data_Schema.md`.

---

## Task 1: WorkflowSignal + FormulaWorkflowSignal entities + SignalFilter column (schema)

**Files:**
- Create: `src/Diariz.Domain/Entities/WorkflowSignal.cs`, `FormulaWorkflowSignal.cs`
- Modify: `src/Diariz.Domain/Entities/WebhookSubscription.cs`, `src/Diariz.Domain/DiarizDbContext.cs`
- Create: `src/Diariz.Domain/Migrations/<ts>_AddWorkflowSignals.cs`
- Test: `tests/Diariz.Api.IntegrationTests/WorkflowSignalSchemaTests.cs`

**Interfaces produced:** `WorkflowSignal`, `FormulaWorkflowSignal` (composite PK); `WebhookSubscription.SignalFilter` (`string?`); DbSets `WorkflowSignals`, `FormulaWorkflowSignals`.

- [ ] **Step 1: Write the failing test**

`tests/Diariz.Api.IntegrationTests/WorkflowSignalSchemaTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WorkflowSignalSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task Signal_links_to_formula_and_deleting_the_signal_removes_the_link_not_the_formula()
    {
        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var formula = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "F" };
        var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = $"k{Guid.NewGuid():N}", Label = "Send to Slack" };
        db.Formulas.Add(formula);
        db.WorkflowSignals.Add(signal);
        db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
        await db.SaveChangesAsync();

        Assert.True(signal.IsActive); // default

        // Deleting the signal removes the link but leaves the formula.
        db.WorkflowSignals.Remove(signal);
        await db.SaveChangesAsync();
        Assert.False(await db.FormulaWorkflowSignals.AnyAsync(x => x.FormulaId == formula.Id));
        Assert.True(await db.Formulas.AnyAsync(f => f.Id == formula.Id));
    }

    [Fact]
    public async Task Subscription_persists_a_signal_filter()
    {
        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Scope = WebhookScope.Platform, Name = "P",
            Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = "post-to-slack",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();
        Assert.Equal("post-to-slack", (await db.Webhooks.SingleAsync(s => s.Id == sub.Id)).SignalFilter);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~WorkflowSignalSchemaTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the entities**

`src/Diariz.Domain/Entities/WorkflowSignal.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>An admin-defined named routing key a formula author can attach to a formula ("Send to Slack").
/// When the formula finishes, the signal's <see cref="Key"/> rides outward on the webhook event and a
/// platform subscription filtering on that key receives it.</summary>
public class WorkflowSignal
{
    public Guid Id { get; set; }

    /// <summary>Stable machine-facing routing slug, unique (e.g. <c>post-to-slack</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Friendly, author-facing label (e.g. "Send to Slack").</summary>
    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Inactive signals are hidden from the author picker but existing links are kept.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/Diariz.Domain/Entities/FormulaWorkflowSignal.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Join: a formula carries zero or more Workflow Signals. Composite key (FormulaId, WorkflowSignalId).</summary>
public class FormulaWorkflowSignal
{
    public Guid FormulaId { get; set; }
    public Formula? Formula { get; set; }
    public Guid WorkflowSignalId { get; set; }
    public WorkflowSignal? WorkflowSignal { get; set; }
}
```

- [ ] **Step 4: Add the SignalFilter column**

In `src/Diariz.Domain/Entities/WebhookSubscription.cs`, after `EventTypes`:

```csharp
    /// <summary>Comma-separated Workflow Signal keys this subscription routes on. Platform subscriptions require
    /// a non-empty filter (they fire only when a signal matches); personal subscriptions may use it to narrow.</summary>
    public string? SignalFilter { get; set; }
```

- [ ] **Step 5: DbSets + model config**

In `src/Diariz.Domain/DiarizDbContext.cs`, add DbSets near the others:

```csharp
    public DbSet<WorkflowSignal> WorkflowSignals => Set<WorkflowSignal>();
    public DbSet<FormulaWorkflowSignal> FormulaWorkflowSignals => Set<FormulaWorkflowSignal>();
```

In `OnModelCreating` (outside the `isNpgsql` guard - plain columns), add:

```csharp
        builder.Entity<WorkflowSignal>(e =>
        {
            e.Property(s => s.Key).HasMaxLength(64);
            e.Property(s => s.Label).HasMaxLength(200);
            e.HasIndex(s => s.Key).IsUnique();
        });
        builder.Entity<FormulaWorkflowSignal>(e =>
        {
            e.HasKey(x => new { x.FormulaId, x.WorkflowSignalId });
            e.HasOne(x => x.Formula).WithMany().HasForeignKey(x => x.FormulaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.WorkflowSignal).WithMany().HasForeignKey(x => x.WorkflowSignalId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WebhookSubscription>().Property(s => s.SignalFilter).HasMaxLength(1024);
```

> Add that last `SignalFilter` line inside the existing `builder.Entity<WebhookSubscription>(e => {...})` block instead, if you prefer - either is fine.

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddWorkflowSignals --project src/Diariz.Domain --startup-project src/Diariz.Api`

New tables + one nullable column - no default hand-editing needed. Confirm `Up` creates `WorkflowSignals` (unique index on `Key`), `FormulaWorkflowSignals` (composite PK, both FKs cascade), and adds `SignalFilter` (nullable) to `Webhooks`.

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~WorkflowSignalSchemaTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/WorkflowSignal.cs src/Diariz.Domain/Entities/FormulaWorkflowSignal.cs src/Diariz.Domain/Entities/WebhookSubscription.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/WorkflowSignalSchemaTests.cs
git commit -m "feat: Workflow Signal entities and subscription SignalFilter (schema)"
```

---

## Task 2: Signal-filter matching helper (pure)

**Files:**
- Create: `src/Diariz.Api/Webhooks/WebhookSignals.cs`
- Test: `tests/Diariz.Api.Tests/WebhookSignalsTests.cs`

**Interfaces produced:** `WebhookSignals.Split(csv)`, `Join(keys)`, `IsEmpty(csv)`, `Intersects(csv, IReadOnlyList<string> signals)`.

- [ ] **Step 1: Write the failing test**

`tests/Diariz.Api.Tests/WebhookSignalsTests.cs`:

```csharp
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookSignalsTests
{
    [Fact]
    public void IsEmpty_true_for_null_or_blank()
    {
        Assert.True(WebhookSignals.IsEmpty(null));
        Assert.True(WebhookSignals.IsEmpty("  "));
        Assert.False(WebhookSignals.IsEmpty("a"));
    }

    [Fact]
    public void Intersects_true_when_a_filter_key_is_in_the_event_signals()
    {
        var filter = WebhookSignals.Join(new[] { "post-to-slack", "file-to-crm" });
        Assert.True(WebhookSignals.Intersects(filter, new[] { "file-to-crm" }));
        Assert.False(WebhookSignals.Intersects(filter, new[] { "other" }));
        Assert.False(WebhookSignals.Intersects(filter, Array.Empty<string>()));
        Assert.False(WebhookSignals.Intersects(null, new[] { "post-to-slack" }));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookSignalsTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`src/Diariz.Api/Webhooks/WebhookSignals.cs`:

```csharp
namespace Diariz.Api.Webhooks;

/// <summary>CSV helpers for a subscription's <c>SignalFilter</c> (the set of Workflow Signal keys it routes on).</summary>
public static class WebhookSignals
{
    public static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> keys) => string.Join(',', keys);

    public static bool IsEmpty(string? csv) => Split(csv).Length == 0;

    /// <summary>True when any key in <paramref name="csv"/> appears in <paramref name="signals"/>.</summary>
    public static bool Intersects(string? csv, IReadOnlyList<string> signals)
    {
        if (signals.Count == 0) return false;
        var filter = Split(csv);
        return filter.Length != 0 && filter.Any(k => signals.Contains(k, StringComparer.Ordinal));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookSignalsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Webhooks/WebhookSignals.cs tests/Diariz.Api.Tests/WebhookSignalsTests.cs
git commit -m "feat: signal-filter matching helper (pure)"
```

---

## Task 3: Publisher - signals param + platform matching + inline output

**Files:**
- Modify: `src/Diariz.Api/Services/WebhookPublisher.cs`, `tests/Diariz.Api.TestSupport/Fakes.cs`
- Test: `tests/Diariz.Api.Tests/WebhookPublisherTests.cs` (extend)

**Interfaces produced:** `IWebhookPublisher.PublishAsync(string eventType, Guid ownerUserId, object data, IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)`.

**Consumes:** `WebhookSignals` (Task 2), `WebhookScope.Platform` + `SignalFilter` (Task 1).

- [ ] **Step 1: Write the failing test** (extend `WebhookPublisherTests`)

```csharp
[Fact]
public async Task Platform_sub_matches_by_signal_and_gets_inline_output_for_formula_events()
{
    var db = TestDb.Create();
    var owner = Guid.NewGuid();
    // A personal sub owned by someone else, and a platform sub routed on "post-to-slack".
    db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Personal,
        Name = "p", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
    db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
        Name = "plat", Url = "https://x/z", SecretEncrypted = "c", EventTypes = "formula_result.completed",
        SignalFilter = "post-to-slack", IsActive = true });
    // An empty-filter platform sub must NOT match.
    db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
        Name = "plat-empty", Url = "https://x/w", SecretEncrypted = "c", EventTypes = "formula_result.completed",
        SignalFilter = null, IsActive = true });
    await db.SaveChangesAsync();

    var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
    await pub.PublishAsync("formula_result.completed", owner,
        data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
        signals: new[] { "post-to-slack" },
        platformData: new { formulaResultId = Guid.NewGuid(), status = "Ready", output = "the output", formulaName = "F" });

    var deliveries = await db.WebhookDeliveries.Include(d => d.Subscription).ToListAsync();
    // Only the signal-matched platform sub fires (owner has no personal sub; other personal belongs to someone else).
    Assert.Single(deliveries);
    Assert.Equal(WebhookScope.Platform, deliveries[0].Subscription!.Scope);
    Assert.Contains("\"output\":\"the output\"", deliveries[0].PayloadJson); // inline output embedded
}

[Fact]
public async Task Personal_delivery_stays_thin_even_when_platform_data_is_supplied()
{
    var db = TestDb.Create();
    var owner = Guid.NewGuid();
    db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = owner, Scope = WebhookScope.Personal,
        Name = "mine", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
    await db.SaveChangesAsync();

    var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
    await pub.PublishAsync("formula_result.completed", owner,
        data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
        signals: new[] { "post-to-slack" },
        platformData: new { output = "secret output" });

    var d = await db.WebhookDeliveries.SingleAsync();
    Assert.DoesNotContain("output", d.PayloadJson); // personal never embeds output
}
```

Add `using Diariz.Domain.Entities;`, `using Microsoft.EntityFrameworkCore;` if needed. The existing Phase 2 publisher tests must still pass (personal-only matching unchanged for recording events).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookPublisherTests"`
Expected: FAIL to compile (new param) / new assertions fail.

- [ ] **Step 3: Update the interface + implementation**

Replace `PublishAsync` in `src/Diariz.Api/Services/WebhookPublisher.cs`:

```csharp
public interface IWebhookPublisher
{
    /// <summary>Enqueues one <see cref="WebhookDelivery"/> per matching subscription. Personal subs (owned by
    /// <paramref name="ownerUserId"/>) get the thin <paramref name="data"/> body; platform subs whose SignalFilter
    /// intersects <paramref name="signals"/> get <paramref name="platformData"/> (the inline-output body) when
    /// supplied, else the thin body. Best-effort: never throws.</summary>
    Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default);
}
```

Body:

```csharp
    public async Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)
    {
        try
        {
            var sig = signals ?? Array.Empty<string>();
            var all = await _db.Webhooks
                .Where(s => s.IsActive
                    && ((s.Scope == WebhookScope.Personal && s.OwnerUserId == ownerUserId)
                        || s.Scope == WebhookScope.Platform))
                .ToListAsync(ct);

            var personal = all.Where(s => s.Scope == WebhookScope.Personal
                && WebhookEventTypes.Matches(s.EventTypes, eventType)
                && (WebhookSignals.IsEmpty(s.SignalFilter) || WebhookSignals.Intersects(s.SignalFilter, sig))).ToList();
            var platform = all.Where(s => s.Scope == WebhookScope.Platform
                && WebhookEventTypes.Matches(s.EventTypes, eventType)
                && WebhookSignals.Intersects(s.SignalFilter, sig)).ToList();   // empty filter -> Intersects false -> no match

            if (personal.Count == 0 && platform.Count == 0) return;

            var eventId = "evt_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var thinBody = WebhookPayload.Build(eventId, eventType, now, data);
            var platformBody = platformData is null ? thinBody : WebhookPayload.Build(eventId, eventType, now, platformData);

            foreach (var s in personal) Enqueue(s, eventId, eventType, thinBody, now);
            foreach (var s in platform) Enqueue(s, eventId, eventType, platformBody, now);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enqueue webhook deliveries for {EventType}", eventType);
        }
    }

    private void Enqueue(WebhookSubscription s, string eventId, string eventType, string body, DateTimeOffset now) =>
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = s.Id, EventId = eventId, EventType = eventType,
            PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = now,
        });
```

Add `using Diariz.Api.Webhooks;` if not present.

- [ ] **Step 4: Update the CapturingWebhookPublisher fake**

In `tests/Diariz.Api.TestSupport/Fakes.cs`:

```csharp
public sealed class CapturingWebhookPublisher : IWebhookPublisher
{
    public readonly List<(string EventType, Guid Owner, object Data, IReadOnlyList<string> Signals, object? PlatformData)> Published = new();
    public Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)
    { Published.Add((eventType, ownerUserId, data, signals ?? Array.Empty<string>(), platformData)); return Task.CompletedTask; }
}
```

Any existing test that reads `Published` tuples by position (e.g. `.EventType`, `.Owner`, `.Data`) keeps working; new fields are appended.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookPublisherTests"` then `dotnet build Diariz.slnx` (catches any other `PublishAsync`/`CapturingWebhookPublisher` consumer). Re-run the Phase 2 emit tests (`RecordingWebhookEmitTests`, `FormulaWebhookEmitTests`) to confirm no regression.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/WebhookPublisher.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/WebhookPublisherTests.cs
git commit -m "feat: platform-scope signal matching and inline output in the publisher"
```

---

## Task 4: Formula events carry signals[] + platform inline output

**Files:**
- Modify: `src/Diariz.Api/Services/FormulaRunProcessor.cs`
- Test: `tests/Diariz.Api.Tests/FormulaWebhookEmitTests.cs` (extend)

**Consumes:** the publisher's new `signals`/`platformData` params (Task 3); `FormulaWorkflowSignal`/`WorkflowSignal` (Task 1).

**Behaviour:** at both emit sites, load the formula's attached **active** signal keys and pass them as `signals`. For `formula_result.completed`, also pass `platformData` = the thin data plus `{ output = result.Text, recordingName, formulaName }`. For `.failed`, add `error` to the thin data and pass `platformData` = thin + `{ recordingName, formulaName }` (no output). `recordingName` = the recording's `Name ?? Title` (fetch it; a section-scoped run with null recordingId passes `recordingName = null`). `formulaName` = the loaded formula's `Name`.

- [ ] **Step 1: Write the failing test** (extend `FormulaWebhookEmitTests`)

Seed a formula with an attached active `WorkflowSignal` (key `post-to-slack`), run a completing formula through `ProcessAsync` with a `CapturingWebhookPublisher`, and assert the captured `formula_result.completed` publish carried `Signals` containing `post-to-slack` and a non-null `PlatformData`. Add a `.failed` case asserting `Signals` is passed too.

```csharp
Assert.Contains(publisher.Published, p =>
    p.EventType == WebhookEventTypes.FormulaResultCompleted
    && p.Signals.Contains("post-to-slack")
    && p.PlatformData is not null);
```

> Use the existing `FormulaWebhookEmitTests` arrange (it already runs `ProcessAsync` with a capturing publisher). Add the join rows: `db.WorkflowSignals.Add(signal)` + `db.FormulaWorkflowSignals.Add(new(){FormulaId=formula.Id, WorkflowSignalId=signal.Id})`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaWebhookEmitTests"`
Expected: FAIL (signals empty / platformData null).

- [ ] **Step 3: Load signals + build platform data at the emit sites**

In `FormulaRunProcessor.ProcessAsync`, before the completed emit, load the signal keys and the recording name once:

```csharp
        var signalKeys = await db.FormulaWorkflowSignals
            .Where(x => x.FormulaId == job.FormulaId && x.WorkflowSignal!.IsActive)
            .Select(x => x.WorkflowSignal!.Key)
            .ToListAsync(ct);
        string? recordingName = job.RecordingId is { } rid
            ? await db.Recordings.Where(r => r.Id == rid).Select(r => r.Name ?? r.Title).FirstOrDefaultAsync(ct)
            : null;
        var formulaName = formula?.Name;
```

Replace the completed `PublishAsync` call:

```csharp
        await webhooks.PublishAsync(WebhookEventTypes.FormulaResultCompleted, job.UserId, new
        {
            recordingId = job.RecordingId, sectionId = job.SectionId, formulaId = job.FormulaId,
            formulaResultId = job.ResultId, signals = signalKeys, status = nameof(FormulaRunStatus.Ready),
            links = new { result = FormulaResultLink(publicUrl, job.RecordingId, job.ResultId) },
        }, signals: signalKeys, platformData: new
        {
            recordingId = job.RecordingId, sectionId = job.SectionId, formulaId = job.FormulaId,
            formulaResultId = job.ResultId, signals = signalKeys, status = nameof(FormulaRunStatus.Ready),
            links = new { result = FormulaResultLink(publicUrl, job.RecordingId, job.ResultId) },
            output = text, recordingName, formulaName,
        });
```

(`text` is the generated body already in scope at this site; `formula` is the loaded formula.)

For the failed site (in `FailAsync`), the `formula`/`text` are not in scope - load the signal keys + names there similarly (query by `job.FormulaId`), add `error` to the data, and pass `platformData` = thin + `{ recordingName, formulaName }` (no `output`). If loading the formula name in `FailAsync` is awkward, pass `signals: signalKeys` with `platformData` carrying just `error`/`recordingName` - the key requirement is `signals[]` is present on the failed event and platform subs still route.

- [ ] **Step 4: Run tests + build**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaWebhookEmitTests"` then `dotnet build Diariz.slnx`.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/FormulaRunProcessor.cs tests/Diariz.Api.Tests/FormulaWebhookEmitTests.cs
git commit -m "feat: formula events carry signals and platform inline output"
```

---

## Task 5: WorkflowSignalsController (list active + admin CRUD)

**Files:**
- Create: `src/Diariz.Api/Controllers/WorkflowSignalsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (signal DTOs)
- Test: `tests/Diariz.Api.Tests/WorkflowSignalsControllerTests.cs`

**DTOs:** `WorkflowSignalDto(Guid Id, string Key, string Label, string? Description, bool IsActive)`; `CreateWorkflowSignalRequest(string Key, string Label, string? Description)`; `UpdateWorkflowSignalRequest(string Label, string? Description, bool IsActive)`.

**Behaviour:** `GET api/workflow-signals` (`[Authorize]`, any user) -> active signals only (for the author picker). `GET api/workflow-signals/manage`, `POST`, `PUT {id}`, `DELETE {id}` all `[Authorize(Policy = "ManagePlatform")]`. Create validates a non-empty, unique, slug-safe `Key` (lowercase letters/digits/hyphen); reject a duplicate key with 409/400.

- [ ] **Step 1: Write the failing tests**

`tests/Diariz.Api.Tests/WorkflowSignalsControllerTests.cs`: create a signal (admin) persists it; list-active excludes inactive; create with a duplicate key is rejected; update toggles `IsActive`; delete removes it. Build the controller with `new WorkflowSignalsController(db)` + `Http.Context(userId)`; the `ManagePlatform` policy is enforced by the pipeline (unit tests call the actions directly, so gate-enforcement is covered by an integration test or by asserting the `[Authorize(Policy=...)]` attribute is present - prefer an integration test in Task's follow-up if the unit harness can't evaluate policies). Focus the unit tests on the CRUD behaviour + key validation.

```csharp
[Fact]
public async Task Create_persists_and_list_active_excludes_inactive()
{
    var db = TestDb.Create();
    var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
    await c.Create(new CreateWorkflowSignalRequest("post-to-slack", "Send to Slack", "desc"));
    var inactive = new WorkflowSignal { Id = Guid.NewGuid(), Key = "off", Label = "Off", IsActive = false };
    db.WorkflowSignals.Add(inactive); await db.SaveChangesAsync();

    var active = (await c.ListActive()).Value!;
    Assert.Single(active);
    Assert.Equal("post-to-slack", active[0].Key);
}

[Fact]
public async Task Create_rejects_duplicate_key()
{
    var db = TestDb.Create();
    var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
    await c.Create(new CreateWorkflowSignalRequest("dup", "A", null));
    var res = await c.Create(new CreateWorkflowSignalRequest("dup", "B", null));
    Assert.IsType<BadRequestObjectResult>(res.Result);
}
```

- [ ] **Step 2: Run to verify it fails** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkflowSignalsControllerTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Add the DTOs** to `ApiDtos.cs`:

```csharp
public record WorkflowSignalDto(Guid Id, string Key, string Label, string? Description, bool IsActive);
public record CreateWorkflowSignalRequest(string Key, string Label, string? Description);
public record UpdateWorkflowSignalRequest(string Label, string? Description, bool IsActive);
```

- [ ] **Step 4: Implement the controller**

`src/Diariz.Api/Controllers/WorkflowSignalsController.cs`:

```csharp
using System.Text.RegularExpressions;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/workflow-signals")]
public class WorkflowSignalsController : ControllerBase
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.Compiled);
    private readonly DiarizDbContext _db;
    public WorkflowSignalsController(DiarizDbContext db) => _db = db;

    /// <summary>Active signals, for the formula-author picker. Any authenticated user.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListActive() =>
        Ok(await _db.WorkflowSignals.Where(s => s.IsActive).OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync());

    [HttpGet("manage")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListAll() =>
        Ok(await _db.WorkflowSignals.OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync());

    [HttpPost]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<WorkflowSignalDto>> Create(CreateWorkflowSignalRequest req)
    {
        var key = (req.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(key)) return BadRequest("Key must be 2-63 chars of lowercase letters, digits, or hyphens.");
        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("A label is required.");
        if (await _db.WorkflowSignals.AnyAsync(s => s.Key == key)) return BadRequest("That key is already in use.");

        var row = new WorkflowSignal { Id = Guid.NewGuid(), Key = key, Label = req.Label.Trim(), Description = req.Description };
        _db.WorkflowSignals.Add(row);
        await _db.SaveChangesAsync();
        return new WorkflowSignalDto(row.Id, row.Key, row.Label, row.Description, row.IsActive);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<WorkflowSignalDto>> Update(Guid id, UpdateWorkflowSignalRequest req)
    {
        var row = await _db.WorkflowSignals.FirstOrDefaultAsync(s => s.Id == id);
        if (row is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("A label is required.");
        row.Label = req.Label.Trim();
        row.Description = req.Description;
        row.IsActive = req.IsActive;   // the Key is immutable once created (it's the routing slug)
        await _db.SaveChangesAsync();
        return new WorkflowSignalDto(row.Id, row.Key, row.Label, row.Description, row.IsActive);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var row = await _db.WorkflowSignals.FirstOrDefaultAsync(s => s.Id == id);
        if (row is null) return NotFound();
        _db.WorkflowSignals.Remove(row); // cascade removes FormulaWorkflowSignal links
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
```

- [ ] **Step 5: Run tests + build** - `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkflowSignalsControllerTests"`, then `dotnet build Diariz.slnx`.

- [ ] **Step 6: (integration) policy-gate test** - add `tests/Diariz.Api.IntegrationTests/WorkflowSignalsAuthTests.cs` proving that a non-admin JWT gets 403 on `POST api/workflow-signals` and 200 on `GET api/workflow-signals`, via `DiarizWebAppFactory`. Run it (Docker).

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Controllers/WorkflowSignalsController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/WorkflowSignalsControllerTests.cs tests/Diariz.Api.IntegrationTests/WorkflowSignalsAuthTests.cs
git commit -m "feat: Workflow Signals API (list active + admin CRUD)"
```

---

## Task 6: Formula signal attachment (FormulasController + FormulaDto)

**Files:**
- Modify: `src/Diariz.Api/Controllers/FormulasController.cs`, `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/FormulasControllerTests.cs` (extend)

**Behaviour:** `CreateFormulaRequest`/`UpdateFormulaRequest` gain `Guid[]? Signals` (Workflow Signal ids). On create/update, reconcile `FormulaWorkflowSignal` rows to exactly that set (ignoring unknown ids). `FormulaDto` gains `Guid[] Signals` (attached ids) so the editor pre-populates. Reconcile only when `Signals` is non-null on update (null = leave unchanged).

- [ ] **Step 1: Write the failing test** - creating a formula with `Signals: [sigId]` persists a `FormulaWorkflowSignal`; updating with `Signals: []` clears them; `FormulaDto.Signals` reflects the attached ids. Follow the existing `FormulasController` test setup.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Extend the DTOs** in `ApiDtos.cs` - add `Guid[]? Signals = null` to `CreateFormulaRequest` and `UpdateFormulaRequest` (append, keep existing params), and `Guid[] Signals` to `FormulaDto` (append).

- [ ] **Step 4: Reconcile in the controller** - add a private helper and call it after save in `Create`/`Update`:

```csharp
    private async Task ReconcileSignalsAsync(Guid formulaId, Guid[]? signalIds)
    {
        if (signalIds is null) return; // unchanged
        var wanted = signalIds.Distinct().ToHashSet();
        var existing = await _db.FormulaWorkflowSignals.Where(x => x.FormulaId == formulaId).ToListAsync();
        _db.FormulaWorkflowSignals.RemoveRange(existing.Where(x => !wanted.Contains(x.WorkflowSignalId)));
        var have = existing.Select(x => x.WorkflowSignalId).ToHashSet();
        var validIds = await _db.WorkflowSignals.Where(s => wanted.Contains(s.Id)).Select(s => s.Id).ToListAsync();
        foreach (var id in validIds.Where(id => !have.Contains(id)))
            _db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formulaId, WorkflowSignalId = id });
        await _db.SaveChangesAsync();
    }
```

Call `await ReconcileSignalsAsync(formula.Id, req.Signals);` after the `SaveChangesAsync()` in both `Create` and `Update`. In `ToDto`, load the attached ids (either eager-load or a small query); adjust `ToDto` to accept the ids, e.g. fetch `await _db.FormulaWorkflowSignals.Where(x => x.FormulaId == f.Id).Select(x => x.WorkflowSignalId).ToArrayAsync()` and pass into the DTO. Keep `ToDto` correct for list endpoints too (batch-load to avoid N+1 if a list endpoint returns many).

- [ ] **Step 5: Run tests + build.**

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/FormulasController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/FormulasControllerTests.cs
git commit -m "feat: attach Workflow Signals to a formula"
```

---

## Task 7: Platform subscriptions admin API

**Files:**
- Create: `src/Diariz.Api/Controllers/PlatformWebhooksController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/PlatformWebhooksControllerTests.cs`

**DTOs:** reuse `WebhookSubscriptionDto` (extend it with `string[] SignalFilter`, `string Scope`); `CreatePlatformWebhookRequest(string? Name, string Url, string[] EventTypes, string[] SignalFilter)`; `UpdatePlatformWebhookRequest(string? Name, string Url, string[] EventTypes, string[] SignalFilter, bool IsActive)`; reuse `WebhookCreatedDto`.

**Behaviour:** `api/admin/webhooks`, `[Authorize(Policy = "ManagePlatform")]`, gated on `WebhooksEnabled`. CRUD for `Scope=Platform` subscriptions. Owner = the creating admin's UserId (note: a platform sub cascades if that admin is deleted - acceptable for v1, flagged as a follow-up). Requires a **non-empty `SignalFilter`** (reject empty - a platform sub with no signal fires on nothing and is a footgun). SSRF-validate the URL; secret shown once; only `WebhookEventTypes.Subscribable` types. Mirror `WebhooksController` structure + reuse `IWebhookSecretProtector`/`IWebhookUrlValidator`.

- [ ] **Step 1: Write the failing tests** - create requires ManagePlatform + a non-empty SignalFilter (empty -> BadRequest); create returns the secret once + persists Scope=Platform + the SignalFilter; list returns platform subs with their signal filter; SSRF url rejected. Mirror `WebhooksControllerTests`.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Extend `WebhookSubscriptionDto`** in `ApiDtos.cs` (append `string Scope, string[] SignalFilter`), add the two platform request records. Update `WebhooksController.ToDto` (personal) to pass `Scope = "Personal"` and `SignalFilter = WebhookSignals.Split(s.SignalFilter)` so the shared DTO is populated consistently.

- [ ] **Step 4: Implement `PlatformWebhooksController`** mirroring `WebhooksController` but: class `[Authorize(Policy = "ManagePlatform")]`; queries filter `Scope == WebhookScope.Platform` (not owner-scoped for read/update/delete - any admin manages any platform sub); `Create` sets `Scope = Platform`, `OwnerUserId = UserId`, `SignalFilter = WebhookSignals.Join(req.SignalFilter)`, rejects an empty `SignalFilter`; reuse the `dz_whsec_` secret mint + `_protector.Protect` + `_urls.ValidateAsync` + the `Subscribable` event-type validation. No `/test` or `/deliveries` needed here unless trivial to add (optional).

- [ ] **Step 5: Run tests + build.**

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/PlatformWebhooksController.cs src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/WebhooksController.cs tests/Diariz.Api.Tests/PlatformWebhooksControllerTests.cs
git commit -m "feat: platform (signal-routed) webhook subscriptions admin API"
```

---

## Task 8: Web types + api client

**Files:** Modify `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`

- [ ] **Step 1: Add TS types** to `types.ts`:

```ts
export interface WorkflowSignal { id: string; key: string; label: string; description: string | null; isActive: boolean; }
export interface CreateWorkflowSignalBody { key: string; label: string; description: string | null; }
export interface UpdateWorkflowSignalBody { label: string; description: string | null; isActive: boolean; }
```

Add `signals: string[]` to the `Formula` interface. Add `signalFilter: string[]` and `scope: "Personal" | "Platform"` to `WebhookSubscription`; add `signals?: string[]` to the formula create/update bodies used in `api.ts`. Add `CreatePlatformWebhookBody { name: string; url: string; eventTypes: string[]; signalFilter: string[]; }` (+ update variant with `isActive`).

- [ ] **Step 2: Add api methods** to `api.ts`:

```ts
async listWorkflowSignals(): Promise<WorkflowSignal[]> {
  const { data } = await http.get<WorkflowSignal[]>("/api/workflow-signals"); return data;
},
async listAllWorkflowSignals(): Promise<WorkflowSignal[]> {
  const { data } = await http.get<WorkflowSignal[]>("/api/workflow-signals/manage"); return data;
},
async createWorkflowSignal(body: CreateWorkflowSignalBody): Promise<WorkflowSignal> {
  const { data } = await http.post<WorkflowSignal>("/api/workflow-signals", body); return data;
},
async updateWorkflowSignal(id: string, body: UpdateWorkflowSignalBody): Promise<WorkflowSignal> {
  const { data } = await http.put<WorkflowSignal>(`/api/workflow-signals/${id}`, body); return data;
},
async deleteWorkflowSignal(id: string): Promise<void> { await http.delete(`/api/workflow-signals/${id}`); },

async listPlatformWebhooks(): Promise<WebhookSubscription[]> {
  const { data } = await http.get<WebhookSubscription[]>("/api/admin/webhooks"); return data;
},
async createPlatformWebhook(body: CreatePlatformWebhookBody): Promise<WebhookCreated> {
  const { data } = await http.post<WebhookCreated>("/api/admin/webhooks", body); return data;
},
async updatePlatformWebhook(id: string, body: CreatePlatformWebhookBody & { isActive: boolean }): Promise<WebhookSubscription> {
  const { data } = await http.put<WebhookSubscription>(`/api/admin/webhooks/${id}`, body); return data;
},
async deletePlatformWebhook(id: string): Promise<void> { await http.delete(`/api/admin/webhooks/${id}`); },
```

Extend `createFormula`/`updateFormula` bodies with `signals?: string[]`. Import the new types.

- [ ] **Step 3: Verify** - `npm run build` clean (from `apps/web`).

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts
git commit -m "feat: web types and api client for workflow signals + platform webhooks"
```

---

## Task 9: Formula editor signal picker

**Files:** Modify `apps/web/src/components/FormulaEditModal.tsx`, `apps/web/src/locales/{en,de,es,fr}/account.json`; Test `FormulaEditModal.test.tsx`

- [ ] **Step 1: Write the failing test** - with `api.listWorkflowSignals` mocked to return one active signal and `webhooksEnabled` true, the editor shows a "When this finishes, trigger" section with the signal's label; ticking it and saving calls `api.createFormula`/`updateFormula` with `signals: [signalId]`. When no active signals exist (or webhooks disabled), the section is hidden. Follow the file's existing test pattern.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** - add `signals` state (string[] of ids), a `useQuery(["workflow-signals"], api.listWorkflowSignals)`, and gate the section on `webhooksEnabled` (read from the user profile query, as the Automations tab does) AND `signals.length > 0`. Render a multi-select of active signals (label + description) mirroring the existing context-checkbox loop. Pass `signals` in the create/update body. Pre-populate from `formula.signals` when editing.

- [ ] **Step 4: i18n** - add keys (all four locales, correct spelling, no dashes): `formulaSignalsHeading` (e.g. "When this finishes, trigger"), `formulaSignalsHint`.

- [ ] **Step 5: Run tests + build** - `npm test -- FormulaEditModal` and `npm run build`.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/FormulaEditModal.tsx apps/web/src/components/FormulaEditModal.test.tsx apps/web/src/locales
git commit -m "feat: attach automation signals in the formula editor"
```

---

## Task 10: Admin Workflow Signals management UI

**Files:** Modify `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/locales/{en,de,es,fr}/account.json`; Test `SettingsModal.test.tsx`

- [ ] **Step 1: Write the failing test** - in the Integration tab, a "Workflow Signals" section lists signals from `api.listAllWorkflowSignals` and creating one (fill key + label, click add) calls `api.createWorkflowSignal`. Toggle-active calls `api.updateWorkflowSignal`; delete calls `api.deleteWorkflowSignal`.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** - a section inside the Integration tab (only meaningful when `webhooksEnabled`): a list of signals (key, label, active toggle, delete) + an add form (key, label, description). Use `useQuery(["workflow-signals-all"], api.listAllWorkflowSignals)` and invalidate on mutate. Keep it compact.

- [ ] **Step 4: i18n** - `signalsHeading`, `signalKey`, `signalLabel`, `signalDescription`, `signalAdd`, `signalActive`, `signalDelete`, `signalKeyHint` (all four locales).

- [ ] **Step 5: Run tests + build** - `npm test -- SettingsModal` and `npm run build`.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/SettingsModal.tsx apps/web/src/components/SettingsModal.test.tsx apps/web/src/locales
git commit -m "feat: admin Workflow Signals management in Settings"
```

---

## Task 11: Admin platform-subscriptions UI

**Files:** Modify `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/locales/{en,de,es,fr}/account.json`; Test `SettingsModal.test.tsx`

- [ ] **Step 1: Write the failing test** - a "Platform automations" section (Integration tab, only when `webhooksEnabled`) lists `api.listPlatformWebhooks`; creating one (name + URL + pick events + pick signals) calls `api.createPlatformWebhook` with the chosen `signalFilter`; the returned secret is shown once; delete calls `api.deletePlatformWebhook`.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** - a section reusing the Automations create-form shape (events checkboxes + URL) plus a **signal multi-select** (from `api.listAllWorkflowSignals`); the create requires at least one signal (client-side guard mirroring the server). Show the secret once. List existing platform subs with their event + signal chips and a delete.

- [ ] **Step 4: i18n** - `platformAutomationsHeading`, `platformAutomationsHint`, `platformAutomationSignals`, `platformAutomationCreate`, `platformAutomationNeedsSignal` (all four locales).

- [ ] **Step 5: Run tests + build** - `npm test -- SettingsModal` and `npm run build`.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/SettingsModal.tsx apps/web/src/components/SettingsModal.test.tsx apps/web/src/locales
git commit -m "feat: admin platform automations (signal-routed webhooks) in Settings"
```

---

## Task 12: Release, docs, verification

**Files:** `version.json` + 3 mirrors; `apps/web/src/lib/releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`; `docs/Data_Schema.md`

- [ ] **Step 1: Bump to `0.154.0`** in all four version files. (Confirm main's current version first; if it moved past 0.153.0, bump to the next Minor and adjust `RELEASES[0].version` to match.)

- [ ] **Step 2: Prepend `RELEASES[0]`** (version == version.json; `pr: 0`):

```ts
  {
    version: "0.154.0",
    date: "2026-07-24",
    pr: 0, // set on open
    headline: "Workflow Signals: route formula outputs to team automations",
    summary:
      "Platform admins can define named Workflow Signals and wire each to a platform automation once, for " +
      "everyone. A formula author just picks 'When this finishes, trigger: Send to Slack' - no URLs or setup - " +
      "and when that formula runs, its output is delivered to the wired destination. Completes the integrations feature.",
    added: [
      "Admin-defined Workflow Signals and a signal picker in the formula editor",
      "Platform (signal-routed) automations that fire across users and can include the formula output inline",
    ],
  },
```

- [ ] **Step 3: CAPABILITIES + docs lockstep** - update the About-box `CAPABILITIES` (Automations/signals row), `README.md` Features + `docs/features.md` bullet; `docs/Overall_Synopsis_of_Platform.md` (Workflow Signals, the platform-scope matching rule, inline output, the admin endpoints); `docs/Data_Schema.md` (`WorkflowSignals`, `FormulaWorkflowSignals`, the `Webhooks.SignalFilter` column, the `AddWorkflowSignals` migration). No em/en dashes in user-facing copy.

- [ ] **Step 4: Run all suites** - `dotnet build Diariz.slnx`; `dotnet test tests/Diariz.Api.Tests`; `dotnet test tests/Diariz.Api.IntegrationTests` (Docker); from `apps/web`: `npm test` + `npm run build`. Report pass counts.

- [ ] **Step 5: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts README.md docs
git commit -m "chore: release 0.154.0 - Workflow Signals and platform automations"
```

- [ ] **Step 6: Push + PR** (controller does this after the final whole-branch review). PR body: deployment surface = server redeploy only; migration additive/forward-restore-safe; **Phase 3 of 3 - completes the integrations feature**. Then set the real PR number into `RELEASES[0].pr`.

---

## Notes for the executor

- **Signature-change sweeps:** Task 3 changes `IWebhookPublisher.PublishAsync` (new params) and the `CapturingWebhookPublisher` fake tuple - grep every consumer (`git grep -n "PublishAsync\|CapturingWebhookPublisher\|\.Published"` in `src`/`tests`) and update. Build `Diariz.slnx` to catch them.
- **Inline output only on platform signal-routed formula deliveries** - personal deliveries must stay thin (the publisher enforces this by only using `platformData` for platform matches; the FormulaRunProcessor builds `platformData` but the publisher decides who gets it). Do not leak `output` to a personal subscriber.
- **Empty-SignalFilter platform sub matches nothing** - this is a deliberate footgun guard, both in the publisher matching (Task 3) and rejected at create time (Task 7).
- **Signal `Key` is immutable after creation** (it's the routing slug that formulas + subscriptions reference by value). The admin edit updates Label/Description/IsActive only.
- **Deferred (documented follow-ups, not built here):**
  - A per-subscription delivery rate cap (spec section 12): the delivery worker already bounds throughput (`BatchSize=20`/2s) and backs off, so a global cap exists; a per-platform-subscription token bucket is a hardening follow-up now that platform subs fan out across users. Note it in the release/docs, do not build it unless trivial.
  - A platform subscription is owned by (and cascades with) the admin who created it - a future improvement is to detach platform subs from a single owner so they survive that admin's deletion.
  - Phase 2 carryover minors (Z vs +00:00 timestamp; N+1 in the delivery worker; delivery-time IP re-pinning) remain open follow-ups.
