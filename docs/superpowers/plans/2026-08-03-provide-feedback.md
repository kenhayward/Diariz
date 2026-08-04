# Provide Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let any signed-in user submit feedback about something that looks or behaves wrong, captured with an automatic technical trail, readable and deletable by a Platform Administrator, and raising an outbound webhook event.

**Architecture:** A ring buffer in the SPA records API calls and navigations, scrubbed on the way in by helpers extracted out of the telemetry module so nothing here depends on the error-tracking SDK. Submission posts description plus trail to a new `FeedbackController`, which stores a `Feedback` row and publishes `feedback.submitted` through the existing `IWebhookPublisher`.

**Tech Stack:** React 19 + TypeScript + Vite, ASP.NET Core 10 + EF Core + Postgres, vitest + @testing-library/react, xUnit.

**Design spec:** `docs/superpowers/specs/2026-08-03-user-problem-reports-design.md`

## Global Constraints

- **Screenshots are out of scope.** Spec section 4.4 is deferred - it needs an Electron shell change and therefore a desktop release. `Feedback.ScreenshotBlobKey` is still added (nullable, always null for now) so the later phase needs no migration.
- **Reading and deleting are Platform Administrator only.** A user may submit and may not list, read or delete - including their own. Enforced server-side with `[Authorize(Policy = "ManagePlatform")]`, the same policy `MaintenanceController` uses. UI gating is not access control.
- **The webhook payload omits the description unless the subscription opts in.** Off by default, mirroring `WebhookSubscription.IncludeAttendeeContacts`. The screenshot is never in a payload.
- **`feedback.submitted` must NOT be personally subscribable** - it stays out of `WebhookEventTypes.Subscribable`. A personal subscription firing on another user's submission would be a disclosure.
- **Trail entries are scrubbed on the way IN.** The buffer must never hold a value that would be unsafe to send.
- **No em dashes or en dashes** (`-` only) in release notes, UI strings, i18n catalogues and help content.
- **Version: 0.175.1 -> 0.176.0** (functional enhancement: Minor +1, Build reset), in `version.json` and all four mirrors.
- **Never commit or push to `main`.** Branch, push, open a PR.
- **Deployment surface: server redeploy, no desktop release.** Nothing under `apps/desktop/**` is touched.
- **Help content is ASCII only** and carries `title` / `summary` / `group` / `order` front matter.

## PR grouping

| PR | Tasks | What |
| --- | --- | --- |
| 1 | 1-3 | The trail (SPA only, no user-visible change) |
| 2 | 4-7 | Storage, API, and the outbound event |
| 3 | 8-10 | The UI, docs and release |

Only PR 3 bumps the version and adds a release entry - 1 and 2 ship no user-visible change on their own. Verify the next free PR number with `gh pr list --state all --limit 1` before writing it into `releases.ts`; do not assume last + 1, since Dependabot shares the sequence.

---

### Task 1: Extract the scrubbers into `lib/scrub.ts`

Pure move. No behaviour change, and the existing tests must pass untouched - that is the proof.

**Files:**
- Create: `apps/web/src/lib/scrub.ts`
- Create: `apps/web/src/lib/scrub.test.ts`
- Modify: `apps/web/src/lib/telemetry.ts`
- Modify: `apps/web/src/lib/telemetry.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: from `apps/web/src/lib/scrub.ts` - `REDACTED: string`, `isSensitiveKey(key: string): boolean`, `stripQueryString(value: string): string`, `scrubUrlsIn(text: string): string`, and `scrubDeep<T>(value: T): T` (the recursive walker, currently the private `scrub`). Tasks 2 and 3 consume `isSensitiveKey`, `stripQueryString` and `scrubDeep`.

**Why this task exists:** `telemetry.ts` statically imports `@sentry/react`. ES imports are hoisted, so importing a scrubber from it pulls the SDK in - the wrong dependency for a trail that must work with telemetry off.

- [ ] **Step 1: Create `scrub.ts` by moving code, not rewriting it**

Cut from `apps/web/src/lib/telemetry.ts` into a new `apps/web/src/lib/scrub.ts`, unchanged:

- the header comment block (the three-copy SYNC OBLIGATION notice)
- `REDACTED`, `DENY_EXACT`, `DENY_SUBSTRING`
- `isSensitiveKey`, `stripQueryString`, `scrubUrlsIn`
- `isWalkable`, and the recursive `scrub` - **renamed `scrubDeep` and exported**, because `scrub` is too generic once it is a shared module

Add to the top of the new file:

```ts
// Imports NOTHING. That is the point: apps/web/src/lib/trail.ts needs these rules and must work with
// the error-tracking SDK absent, and telemetry.ts statically imports @sentry/react. ES imports are
// hoisted, so a trail that imported from telemetry.ts would drag the SDK in regardless of DSN.
```

- [ ] **Step 2: Re-point `telemetry.ts` at the new module**

Replace the removed definitions with an import, and keep the local alias so the rest of the file is untouched:

```ts
import { REDACTED, isSensitiveKey, stripQueryString, scrubUrlsIn, scrubDeep } from "./scrub";

// Local alias: the rest of this file calls the walker `scrub`.
const scrub = scrubDeep;
```

Re-export the four public names so any existing importer of `telemetry.ts` still resolves:

```ts
export { REDACTED, isSensitiveKey, stripQueryString, scrubUrlsIn };
```

- [ ] **Step 3: Move the scrubber tests, unchanged**

Move these `describe` blocks from `apps/web/src/lib/telemetry.test.ts` into a new `apps/web/src/lib/scrub.test.ts`, changing **only** the import path to `./scrub`:

- `isSensitiveKey`
- `stripQueryString`
- `scrubUrlsIn`
- `REDACTED`
- the cross-runtime deny-list parity test (`test_the_shared_cross_runtime_deny_list_is_covered`'s TypeScript equivalent)

Leave `beforeSend`, `beforeBreadcrumb`, `beforeSendTransaction` and `initTelemetry` in `telemetry.test.ts` - they test SDK wiring, not the rules.

Do not change a single assertion. If one needs changing, the move was not a move.

- [ ] **Step 4: Run both suites**

```bash
cd apps/web && npx vitest run src/lib/scrub.test.ts src/lib/telemetry.test.ts
```

Expected: PASS, with the same total count as before the split.

- [ ] **Step 5: Update the three-copy sync comments**

The header now lives in `scrub.ts`. Update the corresponding comments in `src/Diariz.Worker/telemetry.py` and `src/Diariz.Api/Services/SentryScrubber.cs` to name `apps/web/src/lib/scrub.ts` instead of `apps/web/src/lib/telemetry.ts`. Both currently point at the old path; a stale pointer in a sync obligation is worse than none.

- [ ] **Step 6: Full suite and build**

```bash
cd apps/web && npx vitest run
```
```bash
cd apps/web && npm run build
```
Expected: both clean, no warnings.

- [ ] **Step 7: Commit**

```bash
git checkout -b feat/feedback-trail
git add apps/web/src/lib/scrub.ts apps/web/src/lib/scrub.test.ts apps/web/src/lib/telemetry.ts apps/web/src/lib/telemetry.test.ts src/Diariz.Worker/telemetry.py src/Diariz.Api/Services/SentryScrubber.cs
git commit -m "refactor: extract the scrubbing rules into lib/scrub.ts"
```

---

### Task 2: `lib/trail.ts` - the ring buffer (TDD)

**Files:**
- Create: `apps/web/src/lib/trail.ts`
- Create: `apps/web/src/lib/trail.test.ts`

**Interfaces:**
- Consumes: `isSensitiveKey`, `stripQueryString`, `scrubDeep` from `./scrub` (Task 1).
- Produces: from `apps/web/src/lib/trail.ts` -
  - `type TrailEntry = { at: number; kind: "api" | "nav" | "mark"; label: string; detail?: Record<string, unknown> }`
  - `record(entry: Omit<TrailEntry, "at">): void`
  - `snapshot(): TrailEntry[]`
  - `clearTrail(): void` (tests only)
  - `TRAIL_CAPACITY: number`

  Task 3 calls `record`. Task 8 calls `snapshot`.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/lib/trail.test.ts`:

```ts
import { describe, it, expect, beforeEach } from "vitest";
import { record, snapshot, clearTrail, TRAIL_CAPACITY } from "./trail";
import { REDACTED } from "./scrub";

beforeEach(() => clearTrail());

describe("ring buffer", () => {
  it("keeps entries in order", () => {
    record({ kind: "nav", label: "/a" });
    record({ kind: "nav", label: "/b" });
    expect(snapshot().map((e) => e.label)).toEqual(["/a", "/b"]);
  });

  it("evicts the oldest past capacity", () => {
    for (let i = 0; i < TRAIL_CAPACITY + 5; i++) record({ kind: "mark", label: `m${i}` });
    const labels = snapshot().map((e) => e.label);
    expect(labels).toHaveLength(TRAIL_CAPACITY);
    expect(labels[0]).toBe("m5");
    expect(labels[labels.length - 1]).toBe(`m${TRAIL_CAPACITY + 4}`);
  });

  it("stamps each entry with a time", () => {
    record({ kind: "mark", label: "x" });
    expect(typeof snapshot()[0].at).toBe("number");
  });

  it("returns a copy, so a caller cannot mutate the buffer", () => {
    record({ kind: "mark", label: "x" });
    snapshot().push({ at: 0, kind: "mark", label: "injected" });
    expect(snapshot()).toHaveLength(1);
  });
});

describe("scrubbing happens on the way IN", () => {
  it("strips a query string from the label", () => {
    record({ kind: "api", label: "GET /hubs/transcription?access_token=A_LIVE_JWT" });
    expect(JSON.stringify(snapshot())).not.toContain("A_LIVE_JWT");
    expect(snapshot()[0].label).toBe("GET /hubs/transcription");
  });

  it("redacts sensitive keys in detail, keeping diagnostics", () => {
    record({ kind: "api", label: "POST /api/x", detail: { status: 200, transcript: "meeting text" } });
    const [entry] = snapshot();
    expect(entry.detail!.status).toBe(200);
    expect(entry.detail!.transcript).toBe(REDACTED);
  });

  it("redacts nested values in detail", () => {
    record({ kind: "api", label: "POST /api/x", detail: { body: { summary: "secret" } } });
    expect(JSON.stringify(snapshot())).not.toContain("secret");
  });

  it("does not throw on a cyclic detail object", () => {
    const cyclic: Record<string, unknown> = { url: "/x" };
    cyclic.self = cyclic;
    expect(() => record({ kind: "api", label: "GET /x", detail: cyclic })).not.toThrow();
  });
});

describe("independence from the error-tracking SDK", () => {
  it("records with no SDK initialised", () => {
    // No initTelemetry() has run in this suite. If trail.ts imported telemetry.ts, or needed the SDK,
    // this file could not have loaded at all.
    record({ kind: "mark", label: "works" });
    expect(snapshot()).toHaveLength(1);
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npx vitest run src/lib/trail.test.ts
```
Expected: FAIL - cannot resolve `./trail`.

- [ ] **Step 3: Write the implementation**

Create `apps/web/src/lib/trail.ts`:

```ts
import { isSensitiveKey, stripQueryString, scrubDeep } from "./scrub";

/**
 * A short rolling record of what the app just did, attached to a feedback submission.
 *
 * Deliberately NOT built on the error-tracking SDK's breadcrumbs: `beforeBreadcrumb` only fires once
 * the SDK is initialised, and feedback has to work on a deployment with no DSN configured. It is fed
 * instead from seams the app already owns - the axios interceptors and the router.
 *
 * Entries are scrubbed on the way IN rather than on the way out. The buffer therefore never holds a
 * value that would be unsafe to send, so there is no export path that someone can forget to cover.
 * Scrubbing on the way out is how several disclosure paths were introduced in the telemetry work.
 */
export type TrailEntry = {
  at: number;
  kind: "api" | "nav" | "mark";
  label: string;
  detail?: Record<string, unknown>;
};

/** Enough to see the sequence that led to a problem; small enough to post and to read. */
export const TRAIL_CAPACITY = 30;

let buffer: TrailEntry[] = [];

export function record(entry: Omit<TrailEntry, "at">): void {
  const safe: TrailEntry = {
    at: Date.now(),
    kind: entry.kind,
    // The label routinely carries a URL, and a URL routinely carries a credential: @microsoft/signalr
    // puts the JWT in `?access_token=` because a browser cannot set a header on a WS handshake.
    label: stripQueryString(String(entry.label ?? "")),
  };
  if (entry.detail) {
    const scrubbed: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(entry.detail)) {
      scrubbed[k] = isSensitiveKey(k) ? "[redacted]" : scrubDeep(v);
    }
    safe.detail = scrubbed;
  }
  buffer.push(safe);
  if (buffer.length > TRAIL_CAPACITY) buffer = buffer.slice(buffer.length - TRAIL_CAPACITY);
}

/** A copy, so a caller cannot mutate the live buffer. */
export function snapshot(): TrailEntry[] {
  return buffer.map((e) => ({ ...e }));
}

/** Tests only. */
export function clearTrail(): void {
  buffer = [];
}
```

Note `"[redacted]"` is written literally rather than importing `REDACTED`, to avoid a circular-looking import in a hot path - if you prefer the import, use it and the test still passes either way.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/lib/trail.test.ts
```
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/trail.ts apps/web/src/lib/trail.test.ts
git commit -m "feat: add the feedback trail ring buffer"
```

---

### Task 3: Feed the trail from axios and the router (TDD)

**Files:**
- Modify: `apps/web/src/lib/api.ts` (the interceptors around lines 100-122)
- Create: `apps/web/src/lib/trailWiring.test.ts`
- Modify: `apps/web/src/App.tsx`

**Interfaces:**
- Consumes: `record` from `./trail` (Task 2).
- Produces: no new exports. Behaviour: every API response and every route change appears in the trail.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/trailWiring.test.ts`:

```ts
import { describe, it, expect, beforeEach, vi } from "vitest";
import MockAdapter from "axios-mock-adapter";
import { http } from "./api";
import { snapshot, clearTrail } from "./trail";

// If axios-mock-adapter is not already a devDependency, drive the interceptors directly instead:
// call `http.interceptors.response.handlers[0].fulfilled({ config, status, ... })`.
let mock: MockAdapter;

beforeEach(() => {
  clearTrail();
  mock = new MockAdapter(http);
});

describe("axios feeds the trail", () => {
  it("records a successful call with method, path and status", async () => {
    mock.onGet("/api/recordings").reply(200, []);
    await http.get("/api/recordings");

    const [entry] = snapshot();
    expect(entry.kind).toBe("api");
    expect(entry.label).toBe("GET /api/recordings");
    expect(entry.detail!.status).toBe(200);
  });

  it("records a failed call, and the failure still propagates", async () => {
    mock.onGet("/api/boom").reply(500);
    await expect(http.get("/api/boom")).rejects.toBeTruthy();

    const [entry] = snapshot();
    expect(entry.detail!.status).toBe(500);
  });

  it("strips a query string from the recorded path", async () => {
    mock.onGet(/\/hubs\/transcription/).reply(200);
    await http.get("/hubs/transcription?access_token=A_LIVE_JWT");

    expect(JSON.stringify(snapshot())).not.toContain("A_LIVE_JWT");
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd apps/web && npx vitest run src/lib/trailWiring.test.ts
```
Expected: FAIL - the trail is empty.

If the run fails instead on a missing `axios-mock-adapter`, add it as a devDependency (`npm i -D axios-mock-adapter`) or rewrite the test to invoke the interceptor handlers directly. Do not skip the test.

- [ ] **Step 3: Record from the response interceptor**

In `apps/web/src/lib/api.ts`, add the import and extend the existing response interceptor. The success path currently returns `response` unchanged and the error path calls `handleAuthError`; both must keep doing exactly that.

```ts
import { record } from "./trail";

// ... existing request interceptor unchanged ...

/// Feeds the feedback trail. Recording must never change what the caller sees: the success path still
/// returns the response untouched, and the error path still rejects. For "this control was in the wrong
/// state", the response status and path are usually what actually diagnoses it - which is why this seam
/// is worth more to a report than a click listener would be.
http.interceptors.response.use(
  (response) => {
    record({
      kind: "api",
      label: `${(response.config.method ?? "get").toUpperCase()} ${response.config.url ?? ""}`,
      detail: { status: response.status },
    });
    return response;
  },
  (error) => {
    if (axios.isAxiosError(error) && error.config) {
      record({
        kind: "api",
        label: `${(error.config.method ?? "get").toUpperCase()} ${error.config.url ?? ""}`,
        detail: { status: error.response?.status ?? 0 },
      });
    }
    handleAuthError(error);
    return Promise.reject(error);
  },
);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/lib/trailWiring.test.ts
```
Expected: all PASS.

- [ ] **Step 5: Record navigations**

In `apps/web/src/App.tsx`, record each route change. Use the router's location hook already in use in that file:

```tsx
import { useLocation } from "react-router-dom";
import { record } from "./lib/trail";

// Inside the App component, alongside the existing hooks:
const location = useLocation();
useEffect(() => {
  record({ kind: "nav", label: location.pathname });
}, [location.pathname]);
```

**Only `pathname`** - never `search`, which is where a token would be. `stripQueryString` would catch it anyway; not passing it is the belt to that braces.

Read `App.tsx` first and place the hook with the others, respecting the Rules of Hooks - a conditional hook here has already caused a route-specific crash in this codebase.

- [ ] **Step 6: Full suite and build**

```bash
cd apps/web && npx vitest run
```
```bash
cd apps/web && npm run build
```

- [ ] **Step 7: Commit and open PR 1**

```bash
git add apps/web/src/lib/api.ts apps/web/src/lib/trailWiring.test.ts apps/web/src/App.tsx apps/web/package.json apps/web/package-lock.json
git commit -m "feat: feed the feedback trail from axios and navigation"
git push -u origin feat/feedback-trail
```

PR description: no user-visible change; the trail is recorded but nothing reads it yet. **Server redeploy, no desktop release.** No version bump - nothing shipped changes behaviour.

---

### Task 4: The `Feedback` entity and migration

**Files:**
- Create: `src/Diariz.Domain/Entities/Feedback.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Create: migration under `src/Diariz.Domain/Migrations/`

**Interfaces:**
- Consumes: `ApplicationUser`.
- Produces: `Diariz.Domain.Entities.Feedback` with `Id`, `UserId`, `CreatedAt`, `Description`, `Route`, `Release`, `TrailJson`, `ScreenshotBlobKey`; and `DiarizDbContext.Feedback`. Tasks 5-7 consume both.

- [ ] **Step 1: Create the entity**

`src/Diariz.Domain/Entities/Feedback.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A user's report that something looked or behaved wrong, captured with the technical trail
/// leading up to it. Distinct from error tracking: nothing threw, so the exception path never saw it.
///
/// <para>Readable and deletable by a Platform Administrator only - including the submitter's own. A
/// per-user view would imply a support conversation this feature does not have.</para></summary>
public class Feedback
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Who submitted it. Cascade-deleted with the user: this is user-authored content and must
    /// disappear with them, like everything else they own.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user's own words. Free text, so it may quote meeting content - which is exactly why
    /// it lives here, under the same retention, backup and deletion rules as the rest of their data,
    /// rather than in an external error tracker.</summary>
    public string Description { get; set; } = "";

    /// <summary>The SPA route at submission.</summary>
    public string Route { get; set; } = "";

    /// <summary>The app version the browser was running.</summary>
    public string Release { get; set; } = "";

    /// <summary>The client trail, already scrubbed browser-side, stored verbatim as JSON.</summary>
    public string TrailJson { get; set; } = "[]";

    /// <summary>Reserved for the deferred screenshot phase, which needs an Electron shell change and so a
    /// desktop release. Added now so that phase needs no migration. Always null today.</summary>
    public string? ScreenshotBlobKey { get; set; }
}
```

- [ ] **Step 2: Register it**

In `src/Diariz.Domain/DiarizDbContext.cs`, alongside the other `DbSet` properties:

```csharp
public DbSet<Feedback> Feedback => Set<Feedback>();
```

And in `OnModelCreating`, the cascade:

```csharp
b.Entity<Feedback>()
    .HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId)
    .OnDelete(DeleteBehavior.Cascade);
```

- [ ] **Step 3: Create the migration**

```bash
dotnet ef migrations add AddFeedback --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Read the generated file. It should create one table with one FK and no other changes. If it contains anything else, something unrelated was pending - stop and find out what.

- [ ] **Step 4: Confirm it is forward-restore-safe**

This adds a table; it drops nothing and reshapes nothing, so an older backup restores cleanly and `MaintenanceController.CurrentFormat` does **not** need bumping. Confirm that reading is right before moving on - the fence exists to stop an old dump being silently corrupted.

- [ ] **Step 5: Build and run the integration suite**

```bash
dotnet build Diariz.slnx
```
```bash
dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: clean. The integration fixture applies migrations, so a broken migration fails here.

- [ ] **Step 6: Commit**

```bash
git checkout -b feat/feedback-api
git add src/Diariz.Domain/Entities/Feedback.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations/
git commit -m "feat: add the Feedback entity"
```

---

### Task 5: `POST /api/feedback` (TDD)

**Files:**
- Create: `src/Diariz.Api/Controllers/FeedbackController.cs`
- Create: `tests/Diariz.Api.Tests/FeedbackControllerTests.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`

**Interfaces:**
- Consumes: `Feedback`, `DiarizDbContext.Feedback` (Task 4).
- Produces: `FeedbackController` and, in `ApiDtos.cs`, `record CreateFeedbackRequest(string Description, string Route, string Release, string TrailJson)` and `record FeedbackDto(Guid Id, Guid UserId, string? UserEmail, DateTimeOffset CreatedAt, string Description, string Route, string Release, string TrailJson)`. Tasks 6-9 consume both.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/FeedbackControllerTests.cs`, following `ActionsControllerTests`' structure (`TestDb.Create()`, `Http.Context(userId)`):

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class FeedbackControllerTests
{
    private static FeedbackController Build(DiarizDbContext db, Guid userId) =>
        new(db, new FakeWebhookPublisher()) { ControllerContext = Http.Context(userId) };

    private static Guid SeedUser(DiarizDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = "u@e.com", UserName = "u@e.com" });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Create_StoresAgainstTheCallingUser()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest(
            "The delete button was enabled", "/recordings/1", "0.176.0", "[]"));

        var row = await db.Feedback.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Equal("The delete button was enabled", row.Description);
        Assert.Equal("/recordings/1", row.Route);
    }

    [Fact]
    public async Task Create_StoresCreatedAtAsUtc()
    {
        // Npgsql rejects a non-zero-offset DateTimeOffset on a timestamptz column. The in-memory
        // provider does not enforce it, so this only guards the shape; the integration suite proves it.
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest("x", "/", "0.176.0", "[]"));

        Assert.Equal(TimeSpan.Zero, (await db.Feedback.SingleAsync()).CreatedAt.Offset);
    }

    [Fact]
    public async Task Create_RejectsAnEmptyDescription()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        var result = await Build(db, userId).Create(new CreateFeedbackRequest("   ", "/", "0.176.0", "[]"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.Feedback);
    }

    [Fact]
    public async Task Create_TruncatesAnOverlongDescription()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest(
            new string('x', FeedbackController.MaxDescription + 500), "/", "0.176.0", "[]"));

        Assert.Equal(FeedbackController.MaxDescription, (await db.Feedback.SingleAsync()).Description.Length);
    }
}
```

`FakeWebhookPublisher` goes in `tests/Diariz.Api.TestSupport` (namespace `Diariz.Api.Tests.Infrastructure`) if one does not already exist there - check first, and reuse it if it does. This project uses hand-rolled fakes, not a mocking library.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FeedbackController"
```
Expected: FAIL to compile - the controller and DTOs do not exist.

- [ ] **Step 3: Add the DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, beside the other request/response records:

```csharp
public record CreateFeedbackRequest(string Description, string Route, string Release, string TrailJson);

public record FeedbackDto(Guid Id, Guid UserId, string? UserEmail, DateTimeOffset CreatedAt,
    string Description, string Route, string Release, string TrailJson);
```

- [ ] **Step 4: Write the controller**

Create `src/Diariz.Api/Controllers/FeedbackController.cs` with the POST only (GET and DELETE are Task 6). Follow `ActionsController` for constructor injection and the `UserId` helper.

```csharp
[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    /// <summary>Cap on stored description length. Generous for a real report, bounded so a paste of an
    /// entire transcript does not become a row nobody can read or delete comfortably.</summary>
    public const int MaxDescription = 4000;

    private readonly DiarizDbContext _db;
    private readonly IWebhookPublisher _webhooks;

    public FeedbackController(DiarizDbContext db, IWebhookPublisher webhooks)
    {
        _db = db;
        _webhooks = webhooks;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateFeedbackRequest req, CancellationToken ct = default)
    {
        var description = (req.Description ?? "").Trim();
        if (description.Length == 0) return BadRequest("Please describe the problem.");
        if (description.Length > MaxDescription) description = description[..MaxDescription];

        var row = new Feedback
        {
            UserId = UserId,
            // Npgsql rejects a non-zero offset on a timestamptz column.
            CreatedAt = DateTimeOffset.UtcNow.ToUniversalTime(),
            Description = description,
            Route = (req.Route ?? "").Trim(),
            Release = (req.Release ?? "").Trim(),
            TrailJson = string.IsNullOrWhiteSpace(req.TrailJson) ? "[]" : req.TrailJson,
        };
        _db.Feedback.Add(row);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = row.Id });
    }
}
```

Add the `UserId` property the same way sibling controllers do (read from the `NameIdentifier` claim) - copy the existing pattern rather than inventing one.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FeedbackController"
```
Expected: 4 PASS.

- [ ] **Step 6: Check the OpenAPI guards**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

These enforce that every published endpoint has a summary, a description, and a tag description, and that the n8n snapshot matches. `/api/feedback` **should** be published - unlike the bootstrap `/api/config`, it is a genuine API surface an automation might use. So add `[EndpointSummary]` / `[EndpointDescription]` following `LanguagesController`, add a `"Feedback"` entry to `OpenApiCuration.TagDescriptions`, and regenerate the n8n snapshot per the test's own instructions.

Confirm the resulting `integrations/n8n-nodes-diariz/` diff is intentional before committing it - that package is published to npm and a version cannot be corrected afterwards.

- [ ] **Step 7: Build and commit**

```bash
dotnet build Diariz.slnx
```

```bash
git add src/Diariz.Api/Controllers/FeedbackController.cs src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/OpenApi/OpenApiCuration.cs tests/Diariz.Api.Tests/FeedbackControllerTests.cs tests/Diariz.Api.TestSupport/ integrations/n8n-nodes-diariz/
git commit -m "feat: accept feedback submissions"
```

---

### Task 6: `GET` and `DELETE`, Platform Administrator only (TDD)

**Files:**
- Modify: `src/Diariz.Api/Controllers/FeedbackController.cs`
- Modify: `tests/Diariz.Api.Tests/FeedbackControllerTests.cs`
- Create: `tests/Diariz.Api.IntegrationTests/FeedbackIntegrationTests.cs`

**Interfaces:**
- Consumes: everything from Task 5.
- Produces: `GET /api/feedback` returning `FeedbackDto[]`, `DELETE /api/feedback/{id}`. Task 9 consumes both.

**The authorisation split matters.** `POST` is `[Authorize]` - any signed-in user. `GET` and `DELETE` are `[Authorize(Policy = "ManagePlatform")]`, the same policy `MaintenanceController` uses. A user cannot list or delete even their own submission.

- [ ] **Step 1: Write the failing unit tests**

Append to `tests/Diariz.Api.Tests/FeedbackControllerTests.cs`:

```csharp
[Fact]
public async Task List_ReturnsNewestFirst_WithSubmitterEmail()
{
    var db = TestDb.Create();
    var userId = SeedUser(db);
    db.Feedback.AddRange(
        new Feedback { UserId = userId, Description = "older", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) },
        new Feedback { UserId = userId, Description = "newer", CreatedAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();

    var result = await Build(db, userId).List();

    var items = Assert.IsType<List<FeedbackDto>>(Assert.IsType<OkObjectResult>(result).Value);
    Assert.Equal(new[] { "newer", "older" }, items.Select(i => i.Description));
    Assert.Equal("u@e.com", items[0].UserEmail);
}

[Fact]
public async Task Delete_RemovesTheRow()
{
    var db = TestDb.Create();
    var userId = SeedUser(db);
    var row = new Feedback { UserId = userId, Description = "x" };
    db.Feedback.Add(row);
    await db.SaveChangesAsync();

    await Build(db, userId).Delete(row.Id);

    Assert.Empty(db.Feedback);
}

[Fact]
public async Task Delete_ReturnsNotFound_ForAnUnknownId()
{
    var db = TestDb.Create();
    var userId = SeedUser(db);

    Assert.IsType<NotFoundResult>(await Build(db, userId).Delete(Guid.NewGuid()));
}
```

The unit tests exercise the method bodies; the **policy** is attribute-driven and cannot be proven here. Step 5 proves it for real.

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FeedbackController"
```
Expected: FAIL to compile - `List` and `Delete` do not exist.

- [ ] **Step 3: Implement**

Add to `FeedbackController`:

```csharp
    /// <summary>Platform Administrator only - deliberately, including a user's own submissions. A
    /// per-user view would imply a support conversation this feature does not have, and would need its
    /// own ownership rules for no benefit.</summary>
    [HttpGet]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var items = await _db.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FeedbackDto(f.Id, f.UserId, f.User!.Email, f.CreatedAt,
                f.Description, f.Route, f.Release, f.TrailJson))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Feedback.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return NotFound();
        _db.Feedback.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
```

- [ ] **Step 4: Run the unit tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FeedbackController"
```
Expected: 7 PASS.

- [ ] **Step 5: Write the integration tests**

The cascade and the policy both need real infrastructure. Create `tests/Diariz.Api.IntegrationTests/FeedbackIntegrationTests.cs`, following the `"integration"` collection pattern the other files in that project use:

```csharp
[Fact]
public async Task DeletingAUser_CascadesTheirFeedback()
{
    // The in-memory provider does not enforce foreign keys, so this is the only place the cascade
    // is actually proven.
    await using var db = fx.CreateDbContext();
    var user = new ApplicationUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@e.com" };
    db.Users.Add(user);
    db.Feedback.Add(new Feedback { UserId = user.Id, Description = "x" });
    await db.SaveChangesAsync();

    db.Users.Remove(user);
    await db.SaveChangesAsync();

    Assert.Empty(await db.Feedback.Where(f => f.UserId == user.Id).ToListAsync());
}

[Fact]
public async Task CreatedAt_RoundTripsThroughPostgres()
{
    // Npgsql throws at SaveChanges on a non-zero-offset DateTimeOffset written to timestamptz.
    await using var db = fx.CreateDbContext();
    var user = new ApplicationUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@e.com" };
    db.Users.Add(user);
    var row = new Feedback { UserId = user.Id, Description = "x", CreatedAt = DateTimeOffset.UtcNow };
    db.Feedback.Add(row);
    await db.SaveChangesAsync();

    Assert.Equal(TimeSpan.Zero, (await db.Feedback.FindAsync(row.Id))!.CreatedAt.Offset);
}
```

Read an existing file in that project first and match its fixture usage exactly.

- [ ] **Step 6: Run the integration suite**

```bash
dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: all PASS (needs Docker).

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Controllers/FeedbackController.cs tests/Diariz.Api.Tests/FeedbackControllerTests.cs tests/Diariz.Api.IntegrationTests/FeedbackIntegrationTests.cs
git commit -m "feat: list and delete feedback, Platform Administrator only"
```

---

### Task 7: The `feedback.submitted` outbound event (TDD)

**Files:**
- Modify: `src/Diariz.Api/Webhooks/WebhookEventTypes.cs`
- Modify: `src/Diariz.Domain/Entities/WebhookSubscription.cs`
- Modify: `src/Diariz.Api/Services/WebhookPublisher.cs`
- Modify: `src/Diariz.Api/Controllers/FeedbackController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Controllers/PlatformWebhooksController.cs`
- Create: migration
- Modify: `tests/Diariz.Api.Tests/WebhookPublisherTests.cs` (or create, if absent)

**Interfaces:**
- Consumes: `IWebhookPublisher.PublishAsync(string eventType, Guid ownerUserId, object data, IReadOnlyList<string>? signals = null, object? platformData = null, object? dataWithContacts = null, CancellationToken ct = default)`.
- Produces: `WebhookEventTypes.FeedbackSubmitted = "feedback.submitted"`, `WebhookSubscription.IncludeFeedbackText`, and a new `dataWithFeedbackText` parameter on `PublishAsync`.

**Read this before starting.** Two rules from the spec, and both are disclosure decisions rather than preferences:

1. **`feedback.submitted` must NOT be added to `WebhookEventTypes.Subscribable`.** That list is what a *personal* subscription may choose. Personal subscriptions receive events about their owner's own data; feedback is Platform-Administrator-only to read, so a personal subscription firing on someone else's submission would be a disclosure.
2. **The description is omitted unless the subscription opts in.** This mirrors `IncludeAttendeeContacts`, whose existing comment reads: *"an automation points at an arbitrary URL, so without this every event would fan the directory's contact details out to whoever owns it."* The same reasoning applies word for word - and without the gate, sending feedback text to an arbitrary URL would undo, through a different door, the decision to keep it out of the error tracker.

- [ ] **Step 1: Write the failing tests**

In `tests/Diariz.Api.Tests/WebhookPublisherTests.cs` (create if absent, following the existing test style):

```csharp
[Fact]
public void FeedbackSubmitted_IsNotPersonallySubscribable()
{
    // A personal subscription receives events about its OWNER's data. Feedback is readable only by a
    // Platform Administrator, so a personal subscription on this type would leak other users' words.
    Assert.DoesNotContain(WebhookEventTypes.FeedbackSubmitted, WebhookEventTypes.Subscribable);
}

[Fact]
public async Task Publish_OmitsFeedbackText_WhenTheSubscriptionHasNotOptedIn()
{
    var db = TestDb.Create();
    db.Webhooks.Add(new WebhookSubscription
    {
        Url = "https://hook.example/x", EventTypes = WebhookEventTypes.FeedbackSubmitted,
        IsActive = true, IncludeFeedbackText = false,
    });
    await db.SaveChangesAsync();

    await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
        WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(),
        data: new { id = Guid.NewGuid(), hasScreenshot = false },
        dataWithFeedbackText: new { id = Guid.NewGuid(), description = "SECRET_FEEDBACK_TEXT" });

    var body = (await db.WebhookDeliveries.SingleAsync()).Payload;
    Assert.DoesNotContain("SECRET_FEEDBACK_TEXT", body);
}

[Fact]
public async Task Publish_IncludesFeedbackText_WhenTheSubscriptionHasOptedIn()
{
    var db = TestDb.Create();
    db.Webhooks.Add(new WebhookSubscription
    {
        Url = "https://hook.example/x", EventTypes = WebhookEventTypes.FeedbackSubmitted,
        IsActive = true, IncludeFeedbackText = true,
    });
    await db.SaveChangesAsync();

    await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
        WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(),
        data: new { id = Guid.NewGuid(), hasScreenshot = false },
        dataWithFeedbackText: new { id = Guid.NewGuid(), description = "SECRET_FEEDBACK_TEXT" });

    Assert.Contains("SECRET_FEEDBACK_TEXT", (await db.WebhookDeliveries.SingleAsync()).Payload);
}
```

Read `WebhookPublisher.cs` and the `WebhookDelivery` entity first: the property holding the serialised body may not be called `Payload`, and the subscription may need more fields set to be considered deliverable. Match what the code actually does.

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookPublisher"
```
Expected: FAIL to compile - `FeedbackSubmitted`, `IncludeFeedbackText` and `dataWithFeedbackText` do not exist.

- [ ] **Step 3: Add the event type**

In `src/Diariz.Api/Webhooks/WebhookEventTypes.cs`:

```csharp
    /// <summary>A user submitted feedback. PLATFORM SUBSCRIPTIONS ONLY - deliberately absent from
    /// <see cref="Subscribable"/>, which is the personal list. A personal subscription receives events
    /// about its owner's own data; feedback is readable only by a Platform Administrator, so a personal
    /// subscription on this type would deliver another user's words to them.</summary>
    public const string FeedbackSubmitted = "feedback.submitted";
```

Do **not** add it to `Subscribable`.

- [ ] **Step 4: Add the opt-in flag and migrate**

In `src/Diariz.Domain/Entities/WebhookSubscription.cs`, beside `IncludeAttendeeContacts`:

```csharp
    /// <summary>Send the user's own words in a <c>feedback.submitted</c> payload. <b>Off by default, and
    /// deliberately opt-in per subscription</b> - the same reasoning as
    /// <see cref="IncludeAttendeeContacts"/>: an automation points at an arbitrary URL, and feedback is
    /// free text that may quote meeting content. Without this the payload carries only ids and context,
    /// and an automation that needs the words fetches them through the API.</summary>
    public bool IncludeFeedbackText { get; set; }
```

```bash
dotnet ef migrations add AddIncludeFeedbackText --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Adds a non-nullable bool with a `false` default - additive, so `CurrentFormat` does not move.

- [ ] **Step 5: Extend the publisher**

In `src/Diariz.Api/Services/WebhookPublisher.cs`, add `object? dataWithFeedbackText = null` to both the interface and the implementation signature, and mirror the existing contacts branch:

```csharp
                if (s.IncludeFeedbackText && dataWithFeedbackText is not null)
                    return feedbackBody ??= WebhookPayload.Build(eventId, eventType, now, dataWithFeedbackText);
```

Place it beside the `IncludeAttendeeContacts` branch and match its lazy-build pattern exactly, so the ordinary case where nobody has opted in still costs nothing. Update the interface's XML doc to describe the new parameter the way the existing one is described.

- [ ] **Step 6: Publish from the controller**

In `FeedbackController.Create`, after `SaveChangesAsync`:

```csharp
        // Best-effort, and after the save: the submission is stored whether or not any automation is
        // listening. The thin body carries no words; only a subscription that has opted in gets those.
        await _webhooks.PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, row.UserId,
            data: new { id = row.Id, route = row.Route, release = row.Release, hasScreenshot = false },
            dataWithFeedbackText: new
            {
                id = row.Id, route = row.Route, release = row.Release, hasScreenshot = false,
                description = row.Description,
            },
            ct: ct);
```

- [ ] **Step 7: Surface the flag on platform subscriptions**

`PlatformWebhooksController` already round-trips `IncludeAttendeeContacts` through its DTOs (`ApiDtos.cs` around lines 646-656). Add `IncludeFeedbackText` alongside it in the same three records and the same mapping, so a Platform Administrator can set it. Follow exactly what the neighbouring field does.

- [ ] **Step 8: Run everything**

```bash
dotnet test tests/Diariz.Api.Tests
```
```bash
dotnet build Diariz.slnx
```
```bash
dotnet test tests/Diariz.Api.IntegrationTests
```

- [ ] **Step 9: Commit and open PR 2**

```bash
git add src/Diariz.Api/Webhooks/WebhookEventTypes.cs src/Diariz.Domain/Entities/WebhookSubscription.cs src/Diariz.Domain/Migrations/ src/Diariz.Api/Services/WebhookPublisher.cs src/Diariz.Api/Controllers/FeedbackController.cs src/Diariz.Api/Controllers/PlatformWebhooksController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/
git commit -m "feat: publish feedback.submitted, with the text opt-in"
git push -u origin feat/feedback-api
```

PR description: **server redeploy, no desktop release.** No version bump - no user-visible change yet. Say explicitly that `feedback.submitted` is platform-only and that the description is off by default.

---

### Task 8: The feedback modal and the menu entry (TDD)

**Files:**
- Create: `apps/web/src/components/FeedbackModal.tsx`
- Create: `apps/web/src/components/FeedbackModal.test.tsx`
- Modify: `apps/web/src/components/UserMenu.tsx`
- Modify: `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/locales/en/*.json` (whichever catalogue holds the account-menu strings)

**Interfaces:**
- Consumes: `snapshot()` from `./lib/trail` (Task 2), `POST /api/feedback` (Task 5).
- Produces: `api.submitFeedback(description: string, route: string, trailJson: string): Promise<{ id: string }>`, and `FeedbackModal` with props `{ onClose: () => void }`. The `release` field is added by the client method from `__APP_VERSION__`, so callers do not pass it.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/FeedbackModal.test.tsx`. Follow `RecordingsPanel.test.tsx`'s pattern - `vi.mock("../lib/api")`, render inside the providers it uses.

```tsx
it("submits the description together with the trail and the route", async () => {
  record({ kind: "nav", label: "/recordings/1" });
  const submit = vi.fn().mockResolvedValue({ id: "abc" });
  vi.mocked(api).submitFeedback = submit;

  render(<FeedbackModal onClose={() => {}} />);
  await userEvent.type(screen.getByRole("textbox"), "The delete button was enabled");
  await userEvent.click(screen.getByRole("button", { name: /send/i }));

  expect(submit).toHaveBeenCalledTimes(1);
  const [description, , trailJson] = submit.mock.calls[0];
  expect(description).toBe("The delete button was enabled");
  expect(trailJson).toContain("/recordings/1");
});

it("will not submit an empty description", async () => {
  const submit = vi.fn();
  vi.mocked(api).submitFeedback = submit;

  render(<FeedbackModal onClose={() => {}} />);
  await userEvent.click(screen.getByRole("button", { name: /send/i }));

  expect(submit).not.toHaveBeenCalled();
});

it("drags by its header and keeps its dialog role", async () => {
  render(<FeedbackModal onClose={() => {}} />);
  const dialog = screen.getByRole("dialog");
  const header = screen.getByTestId("feedback-drag-handle");

  fireEvent.mouseDown(header, { clientX: 100, clientY: 100 });
  fireEvent.mouseMove(window, { clientX: 160, clientY: 140 });
  fireEvent.mouseUp(window);

  // Moved, and still a dialog - dragging must not cost the accessibility contract.
  expect(dialog.style.transform).not.toBe("");
  expect(screen.getByRole("dialog")).toBeTruthy();
});

it("closes on Escape after being dragged", async () => {
  const onClose = vi.fn();
  render(<FeedbackModal onClose={onClose} />);
  fireEvent.mouseDown(screen.getByTestId("feedback-drag-handle"), { clientX: 10, clientY: 10 });
  fireEvent.mouseMove(window, { clientX: 60, clientY: 60 });
  fireEvent.mouseUp(window);

  await userEvent.keyboard("{Escape}");
  expect(onClose).toHaveBeenCalled();
});
```

- [ ] **Step 2: Run them to verify they fail**

```bash
cd apps/web && npx vitest run src/components/FeedbackModal.test.tsx
```
Expected: FAIL - cannot resolve `./FeedbackModal`.

- [ ] **Step 3: Add the API client method**

In `apps/web/src/lib/api.ts`, inside the `api` object:

```ts
  async submitFeedback(description: string, route: string, trailJson: string): Promise<{ id: string }> {
    const { data } = await http.post<{ id: string }>("/api/feedback", {
      description, route, release: __APP_VERSION__, trailJson,
    });
    return data;
  },
```

- [ ] **Step 4: Write the modal**

Create `apps/web/src/components/FeedbackModal.tsx`. Requirements, all of which have a test above:

- `role="dialog"`, `aria-modal`, closes on `Escape`, focus trapped - match whatever the existing modals in this folder do rather than inventing a pattern.
- A drag handle on the header only, `data-testid="feedback-drag-handle"`, moving the dialog by CSS `transform`. **Not** the body: it holds the textarea, and dragging from there would fight text selection.
- Clamp the position so the dialog cannot be dragged fully off-screen.
- Position resets each time it opens; it is not persisted.
- Submit calls `api.submitFeedback(description, window.location.pathname, JSON.stringify(snapshot()))`.
- Placeholder text steering people away from pasting meeting content, e.g. *"Describe what looked wrong. Please avoid pasting meeting content - the technical detail is captured automatically."*
- All user-visible strings via `t(...)` from the i18n catalogue, **no em or en dashes**.

Explain in a comment why it is draggable: the deferred screenshot phase captures the screen, and a dialog over the thing being reported makes it uncapturable.

- [ ] **Step 5: Add the menu entry**

In `apps/web/src/components/UserMenu.tsx`, beside the About row (around line 211):

```tsx
<MenuRow label={t("provideFeedback")} onSelect={run(() => setFeedbackOpen(true))} />
```

and render `{feedbackOpen && <FeedbackModal onClose={() => setFeedbackOpen(false)} />}` next to the existing `AboutModal`. **Not** gated on `isPlatformAdmin` - any signed-in user may submit.

Add `provideFeedback` to the English catalogue with the value `Provide Feedback`. The locale gate in CI fails a PR that adds a key to more than one language, so add English only.

- [ ] **Step 6: Run the tests and the build**

```bash
cd apps/web && npx vitest run
```
```bash
cd apps/web && npm run build
```

- [ ] **Step 7: Commit**

```bash
git checkout -b feat/feedback-ui
git add apps/web/src/components/FeedbackModal.tsx apps/web/src/components/FeedbackModal.test.tsx apps/web/src/components/UserMenu.tsx apps/web/src/lib/api.ts apps/web/src/locales/
git commit -m "feat: add the Provide Feedback modal"
```

---

### Task 9: The admin panel (TDD)

**Files:**
- Create: `apps/web/src/components/FeedbackPanel.tsx`
- Create: `apps/web/src/components/FeedbackPanel.test.tsx`
- Modify: `apps/web/src/components/SettingsModal.tsx`
- Modify: `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/locales/en/*.json`

**Interfaces:**
- Consumes: `GET`/`DELETE /api/feedback` (Task 6).
- Produces: `api.listFeedback(): Promise<FeedbackDto[]>`, `api.deleteFeedback(id: string): Promise<void>`, and `FeedbackPanel`.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/FeedbackPanel.test.tsx`, following `RecordingsPanel.test.tsx`:

```tsx
it("lists submissions newest first with the submitter", async () => {
  vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
    { id: "1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "newer", route: "/x", release: "0.176.0", trailJson: "[]" },
    { id: "2", userEmail: "b@e.com", createdAt: "2026-08-03T09:00:00Z", description: "older", route: "/y", release: "0.176.0", trailJson: "[]" },
  ]);

  render(<FeedbackPanel />, { wrapper: Providers });

  expect((await screen.findAllByTestId("feedback-row")).map((r) => r.textContent)).toHaveLength(2);
  expect(screen.getByText("a@e.com")).toBeTruthy();
});

it("expands a row to show the trail", async () => {
  vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
    { id: "1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0",
      trailJson: JSON.stringify([{ at: 1, kind: "api", label: "GET /api/recordings", detail: { status: 200 } }]) },
  ]);

  render(<FeedbackPanel />, { wrapper: Providers });
  await userEvent.click(await screen.findByRole("button", { name: /detail/i }));

  expect(screen.getByText(/GET \/api\/recordings/)).toBeTruthy();
});

it("deletes after confirmation and refreshes", async () => {
  const del = vi.fn().mockResolvedValue(undefined);
  vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
    { id: "1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0", trailJson: "[]" },
  ]);
  vi.mocked(api).deleteFeedback = del;

  render(<FeedbackPanel />, { wrapper: Providers });
  await userEvent.click(await screen.findByRole("button", { name: /delete/i }));
  await userEvent.click(screen.getByRole("button", { name: /confirm/i }));

  expect(del).toHaveBeenCalledWith("1");
});
```

Read `MaintenancePanel.test.tsx` first and match how it provides its wrapper and mocks - reuse that rather than inventing `Providers`.

- [ ] **Step 2: Run them to verify they fail**

```bash
cd apps/web && npx vitest run src/components/FeedbackPanel.test.tsx
```
Expected: FAIL - cannot resolve `./FeedbackPanel`.

- [ ] **Step 3: Add the API client methods**

```ts
  async listFeedback(): Promise<FeedbackDto[]> {
    const { data } = await http.get<FeedbackDto[]>("/api/feedback");
    return data;
  },

  async deleteFeedback(id: string): Promise<void> {
    await http.delete(`/api/feedback/${id}`);
  },
```

Add the matching `FeedbackDto` TypeScript type to the types module `api.ts` already imports from.

- [ ] **Step 4: Write the panel**

Create `apps/web/src/components/FeedbackPanel.tsx` following `MaintenancePanel.tsx`: react-query for the list, newest first, `data-testid="feedback-row"` per row, an expander rendering the parsed `trailJson` as a readable list, and a delete button with a confirmation step. Strings via `t(...)`, no em or en dashes.

- [ ] **Step 5: Add the tab**

In `apps/web/src/components/SettingsModal.tsx`:

```tsx
type Tab = "ai" | "quotas" | "maintenance" | "integration" | "feedback";
```

Add a `TabButton` for it beside the maintenance one (line ~145), render `<FeedbackPanel />` in the conditional chain (line ~267), and add a `feedbackTab` key to the English catalogue with the value `Feedback`.

The modal is already Platform Administrator only, so the tab needs no additional gating.

- [ ] **Step 6: Run the tests and the build**

```bash
cd apps/web && npx vitest run
```
```bash
cd apps/web && npm run build
```

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/FeedbackPanel.tsx apps/web/src/components/FeedbackPanel.test.tsx apps/web/src/components/SettingsModal.tsx apps/web/src/lib/api.ts apps/web/src/locales/
git commit -m "feat: add the Feedback tab to platform settings"
```

---

### Task 10: Docs and release (PR 3 close-out)

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `apps/web/src/components/AboutModal.tsx`
- Modify: `README.md`, `docs/features.md`, `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Create: `apps/web/src/content/help/en/provide-feedback.md`

- [ ] **Step 1: Bump the version**

**0.175.1 -> 0.176.0** in all five files. This is a functional enhancement, so Minor +1 and Build resets to 0.

- [ ] **Step 2: Add the release entry**

Top of `RELEASES` in `apps/web/src/lib/releases.ts`. Verify the PR number first with `gh pr list --state all --limit 1`.

```ts
{
  version: "0.176.0",
  date: "<today, YYYY-MM-DD>",
  pr: 0,   // replace with the real number
  headline: "Tell us when something looks wrong",
  summary:
    "Some problems never raise an error - a button that should be disabled, a value that looks wrong. " +
    "There is now a Provide Feedback option in your account menu. Describe what you saw and it is sent " +
    "with a short technical trail of what the app just did, which is usually what makes it diagnosable. " +
    "Feedback is readable only by a platform administrator, and can raise an automation so it reaches " +
    "the right place.",
  added: [
    "Provide Feedback in the account menu, with an automatic technical trail.",
    "A Feedback tab in platform settings for reading and deleting submissions.",
    "A feedback.submitted webhook event for automations. The submitter's words are only included when a subscription opts in.",
  ],
},
```

- [ ] **Step 3: Update `Data_Schema.md`**

Add the `Feedback` table with every column, the FK to `AspNetUsers` and its cascade, and the new `IncludeFeedbackText` column on the webhook subscription table. Add both migrations to the migration-history table. Note `ScreenshotBlobKey` is reserved and currently always null.

- [ ] **Step 4: Update the feature docs, in lockstep**

All three, or none - they are a set:

- **README Features table**: one concise row.
- **`docs/features.md`**: the matching prose bullet.
- **`CAPABILITIES` in `releases.ts`**: one row in the two-column table.

- [ ] **Step 5: Update `Overall_Synopsis_of_Platform.md`**

The new endpoints, the Platform-Administrator-only read/delete, the trail's independence from the error-tracking stack, and the new outbound event type - including that it is platform-only and text-gated.

- [ ] **Step 6: Write the help article**

Create `apps/web/src/content/help/en/provide-feedback.md` with `title` / `summary` / `group` / `order` front matter, **ASCII only**. Cover what to use it for, that a technical trail is attached automatically, that only a platform administrator can read it, and - plainly - that they should not paste meeting content because it is stored with the submission.

Keep `summary` to two or three sentences: it is what the contextual `?` popover shows.

- [ ] **Step 7: Verify everything**

```bash
cd apps/web && npx vitest run
```
```bash
cd apps/web && npm run build
```
```bash
dotnet build Diariz.slnx
```
```bash
dotnet test tests/Diariz.Api.Tests
```
```bash
dotnet test tests/Diariz.Api.IntegrationTests
```

`versionMirrors`, `releases` and `helpContent` all run inside the vitest suite and will fail on a missed mirror, a version mismatch, or malformed help front matter.

- [ ] **Step 8: Commit and open PR 3**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts apps/web/src/components/AboutModal.tsx README.md docs/ apps/web/src/content/help/
git commit -m "feat: release Provide Feedback"
git push -u origin feat/feedback-ui
```

PR description: **server redeploy, no desktop release.** Note that screenshots are deliberately deferred because they need an Electron shell change.

---

## Self-review notes

**Spec coverage.** Every section maps to a task: 4.1 flow (Tasks 5-9), 4.2 scrub extraction (Task 1), 4.3 the trail (Tasks 2-3), 4.5 the movable modal (Task 8), 4.6 domain and API (Tasks 4-6), 4.7 the outbound event (Task 7), 4.8 the admin view (Task 9), 4.9 the entry point (Task 8), 5 privacy (Task 7's gating tests, Task 8's placeholder, Task 10's help article), 6 testing (distributed), 7 obligations (Task 10), 8 risks (mitigations across the above).

**Deliberately excluded**, per the Global Constraints: spec section 4.4, the screenshot. `ScreenshotBlobKey` is added now so the deferred phase needs no migration, and the webhook payload already carries `hasScreenshot: false`.

**Deferred to implementation:** the exact `WebhookDelivery` property names in Task 7's tests, and the `Providers` wrapper in Task 9's - both say to read the neighbouring code rather than trust this document, because both are shapes I have not verified line by line.
