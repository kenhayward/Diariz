# Observability Phase 2 (SPA) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report browser crashes and client-perceived API timings from the React SPA to the same GlitchTip instance the worker and API already use, without transmitting meeting content or credentials.

**Architecture:** A single `apps/web/src/lib/telemetry.ts` owns all SDK knowledge, mirroring `src/Diariz.Worker/telemetry.py` and `src/Diariz.Api/Services/SentryScrubber.cs`. The browser DSN cannot be a build-time constant (one image serves both dev and prod), so the API serves it at runtime from a new anonymous `GET /api/config`.

**Tech Stack:** `@sentry/react`, React 19, Vite 8, ASP.NET Core 10, vitest + @testing-library/react.

**Design spec:** `docs/superpowers/specs/2026-07-31-observability-glitchtip-design.md` (section 6)
**Phase 1 plan (completed):** `docs/superpowers/plans/2026-07-31-observability-phase1-worker-api.md`

## Global Constraints

- **Off by default.** Empty DSN means no SDK init and no network call. The SPA must work identically with telemetry absent.
- **`sendDefaultPii: false`** and **`autoSessionTracking: false`** (GlitchTip does not support sessions).
- **No transcript, summary, minutes or note content may leave the browser.** Ids only.
- **No credential may leave the browser.** See the JWT constraint below - it is the single most important requirement in this plan.
- **TDD is required.** Failing test first, watched fail, then implement.
- **No em dashes or en dashes** (`-` only) in release notes and user-facing copy.
- **Every PR ships exactly one release:** bump `version.json` **and all four mirrors**, add one `RELEASES[0]` entry. Version at plan time: **0.174.3** (after phase 1).
- **Never commit or push to `main`.** Branch, push, open a PR.
- **Deployment surface for every PR here: server redeploy, no desktop release.** The Electron shell loads the SPA from the server origin, so desktop users pick these changes up automatically. Nothing under `apps/desktop/src/**` is touched.

## THE constraint: the JWT travels in query strings

Phase 1 found the same leak twice, in two runtimes. **It will occur a third time in the browser, and this plan exists partly to stop it.**

`apps/web/src/lib/signalr.ts` passes `accessTokenFactory`, so `@microsoft/signalr` appends **`?access_token=<JWT>`** to both the `/hubs/transcription` negotiate request and the WebSocket URL. Browsers cannot set an `Authorization` header on a WS handshake, so this is by design and cannot be removed.

`@sentry/react`'s default integrations capture request URLs in at least three places:

| Channel | What it captures |
| --- | --- |
| `Breadcrumbs` integration (`fetch`/`xhr`) | The request URL of every API and negotiate call |
| `BrowserTracing` | Span descriptions containing request URLs |
| `GlobalHandlers` / `event.request.url` | The page URL |

**A key-name deny-list cannot reach any of these** - a query string is one opaque value, not a named field. Every one must be handled by explicit URL stripping. This is the same lesson phase 1 learned twice; do not relearn it.

## Carried forward from phase 1

Three things phase 1 established that bind this plan:

1. **A key-name deny-list only catches what arrives as a named key.** Each runtime has its own channels that bypass it - Python had stack-frame locals, .NET had transactions and query strings. Enumerate the browser's channels explicitly (Task 3) rather than assuming the deny-list suffices.
2. **The deny-list now exists in two hand-synced copies.** This plan adds a third. It must join the same shared set and the same parity tests.
3. **Phase 1 parked a known gap:** no test pins the hook wiring in `Program.cs` - deleting a hook line passes all 1808 tests. In the browser this *is* testable, because init can live in a plain module rather than a host builder. **Task 3 closes it on the web side, and Task 1 closes it for the new API endpoint.** The `Program.cs` gap itself remains parked.

## PR grouping

| PR | Tasks | Version | Sub-phase |
| --- | --- | --- | --- |
| 4 | Tasks 1-4 | 0.174.4 | 2a - DSN delivery + browser error reporting |
| 5 | Tasks 5-6 | 0.174.5 | 2b - browser tracing |
| 6 | Tasks 7-8 | 0.174.6 | 2c - private source maps |

PR numbers 389-392 are taken by phase 1, so the expected numbers are **393**, **394**, **395**. Verify each with `gh pr list --state all --limit 1` before writing it into `releases.ts`; do not guess blindly, since Dependabot PRs share the sequence.

**PR 6 is genuinely optional** and can be dropped without affecting PRs 4-5. See Task 7's preamble.

---

### Task 1: API serves the browser config (TDD)

**Files:**
- Create: `src/Diariz.Api/Controllers/ConfigController.cs`
- Test: `tests/Diariz.Api.Tests/ConfigControllerTests.cs`

**Interfaces:**
- Consumes: `TelemetryOptions` from phase 1 (`src/Diariz.Api/Configuration/AppOptions.cs`), which already has `BrowserDsn`, `Environment` and `TracesSampleRate` properties.
- Produces: `GET /api/config` returning `{ sentryDsn: string, sentryEnvironment: string, sentryTracesSampleRate: double }`. Task 3 consumes it.

**Why an endpoint rather than a build-time constant:** the SPA uses no `import.meta.env` values and `apps/web/Dockerfile` takes only `BUILD_COMMIT`, so a baked DSN would give one image one DSN - forcing dev and prod to share a GlitchTip instance, which the design explicitly rejects. Browser DSNs are public by design (they ship in the JavaScript bundle), so serving one over an anonymous endpoint leaks nothing.

**Why not `/health`:** `/health` is the Docker healthcheck, polled every 15 seconds. Conflating configuration with liveness makes both harder to reason about.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/ConfigControllerTests.cs`:

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ConfigControllerTests
{
    private static ConfigController Build(TelemetryOptions telemetry) =>
        new(Options.Create(telemetry));

    [Fact]
    public void Get_ReturnsTheBrowserDsn_WhenConfigured()
    {
        var result = Build(new TelemetryOptions
        {
            BrowserDsn = "https://key@errors.example/2",
            Environment = "production",
            TracesSampleRate = 0.5,
        }).Get();

        var value = Assert.IsType<OkObjectResult>(result).Value!;
        var dto = Assert.IsType<ClientConfig>(value);
        Assert.Equal("https://key@errors.example/2", dto.SentryDsn);
        Assert.Equal("production", dto.SentryEnvironment);
        Assert.Equal(0.5, dto.SentryTracesSampleRate);
    }

    [Fact]
    public void Get_ReturnsAnEmptyDsn_WhenNotConfigured()
    {
        var result = Build(new TelemetryOptions()).Get();

        var dto = Assert.IsType<ClientConfig>(Assert.IsType<OkObjectResult>(result).Value!);
        Assert.Equal("", dto.SentryDsn);
    }

    [Fact]
    public void Get_NeverReturnsTheServerSideDsn()
    {
        // The server DSN belongs to a different GlitchTip project and must not reach the browser.
        var result = Build(new TelemetryOptions
        {
            Dsn = "https://server-secret@errors.example/1",
            BrowserDsn = "https://browser@errors.example/2",
        }).Get();

        var dto = Assert.IsType<ClientConfig>(Assert.IsType<OkObjectResult>(result).Value!);
        Assert.Equal("https://browser@errors.example/2", dto.SentryDsn);
        Assert.DoesNotContain("server-secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ConfigController"`
Expected: FAIL to compile - `ConfigController` and `ClientConfig` do not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Diariz.Api/Controllers/ConfigController.cs`:

```csharp
using Diariz.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Non-secret configuration the SPA needs before it can boot. Anonymous by necessity: the
/// browser reads this before anyone has signed in.
///
/// This exists because the SPA is built once into one image that serves every environment, so a
/// build-time constant would force dev and production to share one error-tracking project. A browser
/// DSN is public by design - it ships inside the JavaScript bundle either way - so serving it here
/// discloses nothing that shipping it in the bundle would not.
///
/// Only add fields here that are safe for an unauthenticated caller to read.</summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class ConfigController : ControllerBase
{
    private readonly TelemetryOptions _telemetry;

    public ConfigController(IOptions<TelemetryOptions> telemetry) => _telemetry = telemetry.Value;

    [HttpGet]
    public IActionResult Get() => Ok(new ClientConfig(
        SentryDsn: _telemetry.BrowserDsn,
        SentryEnvironment: _telemetry.Environment,
        SentryTracesSampleRate: _telemetry.TracesSampleRate));
}

/// <summary>Browser-safe configuration. Every field here is readable by an anonymous caller - never add
/// the server-side <see cref="TelemetryOptions.Dsn"/>, an API key, or anything user-scoped.</summary>
public record ClientConfig(string SentryDsn, string SentryEnvironment, double SentryTracesSampleRate);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ConfigController"`
Expected: 3 PASS.

- [ ] **Step 5: Confirm the endpoint is genuinely anonymous**

The API has both JWT authentication and an API-token scope middleware. Read `src/Diariz.Api/Program.cs` and `src/Diariz.Api/Auth/ApiTokenScopeMiddleware.cs` and confirm `/api/config` is reachable without credentials and is not caught by a scope requirement. If a fallback authorization policy would block it, `[AllowAnonymous]` must be sufficient - verify rather than assume, and if it is not, make it so.

- [ ] **Step 6: Build the whole solution and run the full unit suite**

```bash
dotnet build Diariz.slnx
```

Building the solution rather than the test project alone is deliberate: a unit-only run misses compile breaks in the integration test project.

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: all pass (1808 + 3 new, 1 pre-existing skip).

- [ ] **Step 7: Commit**

```bash
git checkout -b feat/observability-spa
git add src/Diariz.Api/Controllers/ConfigController.cs tests/Diariz.Api.Tests/ConfigControllerTests.cs
git commit -m "feat: serve browser telemetry config at /api/config"
```

---

### Task 2: Web scrubber (TDD)

**Files:**
- Create: `apps/web/src/lib/telemetry.ts`
- Test: `apps/web/src/lib/telemetry.test.ts`
- Modify: `src/Diariz.Worker/tests/test_telemetry.py` (extend the shared-set test)
- Modify: `tests/Diariz.Api.Tests/SentryScrubberTests.cs` (extend the shared-set test)

**Interfaces:**
- Consumes: nothing.
- Produces: `isSensitiveKey(key: string): boolean`, `stripQueryString(value: string): string`, `scrubUrlsIn(text: string): string`, `REDACTED: string` from `apps/web/src/lib/telemetry.ts`. Task 3 consumes all four.

**This is the third copy of the deny-list.** `src/Diariz.Worker/telemetry.py` and `src/Diariz.Api/Services/SentryScrubber.cs` already carry it, each with a sync comment naming the other. Both comments must be updated to name this third file, and all three parity tests must cover the same set.

- [ ] **Step 1: Read the two existing implementations first**

Read `src/Diariz.Worker/telemetry.py` and `src/Diariz.Api/Services/SentryScrubber.cs` in full before writing anything. The deny-lists, the `REDACTED` constant value, and the URL-stripping behaviour must match. Note in particular:

- the exact-match set and the substring set, which are separate
- that `stripQueryString` keeps the path and drops only the query, because the path is diagnostically useful and carries no credential
- that non-URL strings must pass through unchanged

- [ ] **Step 2: Write the failing test**

Create `apps/web/src/lib/telemetry.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { isSensitiveKey, stripQueryString, scrubUrlsIn, REDACTED } from "./telemetry";

describe("isSensitiveKey", () => {
  it.each(["Authorization", "cookie", "password", "apiKey", "access_key", "accessKey", "token", "secret"])(
    "treats %s as a credential",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["text", "transcript", "transcription", "segments", "words", "summary", "minutes", "note", "notes", "content"])(
    "treats %s as meeting content",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["embedding", "embeddings"])("treats %s as a biometric voiceprint", (key) =>
    expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["recordingId", "transcriptionId", "blobKey", "userId", "model", "language", "status"])(
    "keeps %s, which is needed to diagnose",
    (key) => expect(isSensitiveKey(key)).toBe(false),
  );
});

describe("stripQueryString", () => {
  it("removes the SignalR access token while keeping the path", () => {
    const stripped = stripQueryString("https://app.example/hubs/transcription?access_token=A_LIVE_JWT");
    expect(stripped).toBe("https://app.example/hubs/transcription");
    expect(stripped).not.toContain("A_LIVE_JWT");
  });

  it("removes a query string from a relative URL", () => {
    expect(stripQueryString("/hubs/transcription?access_token=A_LIVE_JWT")).toBe("/hubs/transcription");
  });

  it("leaves a URL with no query string untouched", () => {
    expect(stripQueryString("https://app.example/api/recordings")).toBe("https://app.example/api/recordings");
  });

  it("leaves a value that is not a URL untouched", () => {
    expect(stripQueryString("select * from recordings where id = ?")).toBe(
      "select * from recordings where id = ?",
    );
  });
});

describe("scrubUrlsIn", () => {
  it("strips the query from a URL embedded in a longer description", () => {
    // Sentry describes fetch spans as "<METHOD> <url>".
    expect(scrubUrlsIn("GET https://app.example/hubs/transcription?access_token=A_LIVE_JWT")).toBe(
      "GET https://app.example/hubs/transcription",
    );
  });

  it("leaves free text alone", () => {
    expect(scrubUrlsIn("Detail panel crashed")).toBe("Detail panel crashed");
  });
});

describe("REDACTED", () => {
  it("matches the marker the other two runtimes use", () => {
    expect(REDACTED).toBe("[redacted]");
  });
});
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/lib/telemetry.test.ts`
Expected: FAIL - cannot resolve `./telemetry`.

- [ ] **Step 4: Write the minimal implementation**

Create `apps/web/src/lib/telemetry.ts`. Match the deny-list contents to the two existing runtimes exactly:

```ts
// Redaction rules for anything sent to the error tracker.
//
// SYNC OBLIGATION: this deny-list exists in three places and they must stay in step:
//   - src/Diariz.Worker/telemetry.py
//   - src/Diariz.Api/Services/SentryScrubber.cs
//   - this file
// Each has a test asserting the shared set below. Those tests catch a REMOVAL from one copy, but
// they cannot catch an ADDITION made to only one copy - if you add an entry here, add it to the
// other two and to their tests.

export const REDACTED = "[redacted]";

// Exact field names that carry meeting content.
const DENY_EXACT = new Set([
  "text", "transcript", "transcription", "segments", "words", "summary",
  "minutes", "note", "notes", "content", "authorization", "cookie", "cookies",
  // ECAPA voiceprint vectors (biometric data identifying a speaker by voice).
  "embedding", "embeddings",
]);

// Substrings marking a credential regardless of the surrounding name.
const DENY_SUBSTRING = ["secret", "token", "password", "api_key", "apikey", "access_key", "accesskey"];

/** True when a field with this name must never leave the browser. */
export function isSensitiveKey(key: string): boolean {
  const lowered = String(key).toLowerCase();
  if (DENY_EXACT.has(lowered)) return true;
  return DENY_SUBSTRING.some((marker) => lowered.includes(marker));
}

/**
 * Drop a URL's query string, keeping its path.
 *
 * Load-bearing, not cosmetic: @microsoft/signalr appends `?access_token=<JWT>` to the hub's negotiate
 * and WebSocket URLs, because a browser cannot set an Authorization header on a WS handshake. A
 * key-name deny-list cannot reach that - a query string is one opaque value, not a named field. The
 * path is kept because it is diagnostically useful and carries no credential.
 *
 * Anything that is not a URL is returned unchanged.
 */
export function stripQueryString(value: string): string {
  const cut = value.indexOf("?");
  if (cut === -1) return value;
  // Only treat it as a URL if what precedes the "?" looks like one; otherwise leave free text alone.
  const head = value.slice(0, cut);
  const isUrl = /^https?:\/\//i.test(head) || head.startsWith("/");
  return isUrl ? head : value;
}

/** Apply stripQueryString to every whitespace-separated token, for descriptions like "GET <url>". */
export function scrubUrlsIn(text: string): string {
  return text.split(" ").map(stripQueryString).join(" ");
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd apps/web && npx vitest run src/lib/telemetry.test.ts`
Expected: all PASS.

- [ ] **Step 6: Update the two existing sync comments**

In `src/Diariz.Worker/telemetry.py` and `src/Diariz.Api/Services/SentryScrubber.cs`, the sync comments currently name one counterpart each. Update both to name all three files, matching the wording used in the new TypeScript file. Do not change either deny-list's contents.

- [ ] **Step 7: Extend both existing parity tests**

The worker has `test_the_shared_cross_runtime_deny_list_is_covered` in `src/Diariz.Worker/tests/test_telemetry.py`, and the API has an equivalent in `tests/Diariz.Api.Tests/SentryScrubberTests.cs`. Read both. If the shared set they assert differs from the set in the new TypeScript file, reconcile all three to the same set and say in your report which entries moved and why.

- [ ] **Step 8: Run all three suites**

```bash
cd apps/web && npx vitest run
```

```bash
cd src/Diariz.Worker && python -m pytest
```

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: all pass, no warnings.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/telemetry.ts apps/web/src/lib/telemetry.test.ts src/Diariz.Worker/telemetry.py src/Diariz.Worker/tests/test_telemetry.py src/Diariz.Api/Services/SentryScrubber.cs tests/Diariz.Api.Tests/SentryScrubberTests.cs
git commit -m "feat: add the web scrubber and sync the three deny-lists"
```

---

### Task 3: Initialise the browser SDK (TDD)

**Files:**
- Modify: `apps/web/src/lib/telemetry.ts`
- Modify: `apps/web/src/lib/telemetry.test.ts`
- Modify: `apps/web/src/main.tsx`
- Modify: `apps/web/src/components/ErrorBoundary.tsx`
- Modify: `apps/web/src/components/ErrorBoundary.test.tsx`
- Modify: `apps/web/package.json`

**Interfaces:**
- Consumes: `isSensitiveKey`, `stripQueryString`, `scrubUrlsIn`, `REDACTED` from Task 2; `GET /api/config` from Task 1.
- Produces: `initTelemetry(): Promise<boolean>`, `captureException(error: unknown, context?: Record<string, unknown>): void`, and the exported hooks `beforeSend`, `beforeBreadcrumb` from `apps/web/src/lib/telemetry.ts`.

**Errors only in this task.** Tracing is Task 5. `browserTracingIntegration` must not be enabled here.

- [ ] **Step 1: Add the dependency**

```bash
cd apps/web && npm install --save @sentry/react
```

Report the resolved version. Note the bundle cost in your report - `@sentry/react` without tracing is roughly 25-30 kB gzipped, and the About box lists bundled libraries (Task 4).

- [ ] **Step 2: Write the failing tests**

Append to `apps/web/src/lib/telemetry.test.ts`:

```ts
import { beforeSend, beforeBreadcrumb, initTelemetry } from "./telemetry";

describe("beforeSend", () => {
  it("strips the access token from the page URL", () => {
    const event = { request: { url: "https://app.example/rooms/1?access_token=A_LIVE_JWT" } } as any;

    const cleaned = beforeSend(event)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.request.url).toBe("https://app.example/rooms/1");
  });

  it("redacts sensitive keys in extra and tags, keeping identifiers", () => {
    const event = {
      extra: { transcript: "the confidential meeting content", recordingId: "rid-1" },
      tags: { authorization: "Bearer abc", model: "large-v3" },
    } as any;

    const cleaned = beforeSend(event)!;

    expect(cleaned.extra.transcript).toBe(REDACTED);
    expect(cleaned.extra.recordingId).toBe("rid-1");
    expect(cleaned.tags.authorization).toBe(REDACTED);
    expect(cleaned.tags.model).toBe("large-v3");
  });

  it("does not throw on a bare event", () => {
    expect(() => beforeSend({} as any)).not.toThrow();
  });
});

describe("beforeBreadcrumb", () => {
  it("strips the access token from a fetch breadcrumb URL", () => {
    const crumb = {
      category: "fetch",
      data: { url: "/hubs/transcription?access_token=A_LIVE_JWT", method: "POST" },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.data.url).toBe("/hubs/transcription");
    expect(cleaned.data.method).toBe("POST");
  });

  it("strips the access token from an xhr breadcrumb URL", () => {
    const crumb = { category: "xhr", data: { url: "/api/x?access_token=A_LIVE_JWT" } } as any;

    expect(JSON.stringify(beforeBreadcrumb(crumb))).not.toContain("A_LIVE_JWT");
  });

  it("drops low-level console breadcrumbs, which can carry arbitrary logged content", () => {
    expect(beforeBreadcrumb({ category: "console", level: "log" } as any)).toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "info" } as any)).toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "debug" } as any)).toBeNull();
  });

  it("keeps console breadcrumbs at warn and error, which are the diagnostic ones", () => {
    expect(beforeBreadcrumb({ category: "console", level: "error" } as any)).not.toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "warning" } as any)).not.toBeNull();
  });

  it("redacts sensitive keys in breadcrumb data", () => {
    const crumb = { category: "custom", data: { summary: "meeting summary", recordingId: "rid-1" } } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(cleaned.data.summary).toBe(REDACTED);
    expect(cleaned.data.recordingId).toBe("rid-1");
  });

  it("does not throw on a bare breadcrumb", () => {
    expect(() => beforeBreadcrumb({} as any)).not.toThrow();
  });
});

// jsdom implements neither `fetch` nor `Response`, and nothing else in this app uses them (every other
// HTTP call goes through axios). So assign a stub rather than spying on a global that does not exist,
// and return a duck-typed object rather than constructing a real Response.
function stubConfig(body: unknown) {
  const fetchStub = vi.fn().mockResolvedValue({ json: async () => body });
  (globalThis as any).fetch = fetchStub;
  return fetchStub;
}

afterEach(() => {
  delete (globalThis as any).fetch;
});

describe("initTelemetry", () => {
  it("does nothing when the API returns an empty DSN", async () => {
    const init = vi.fn();
    stubConfig({ sentryDsn: "", sentryEnvironment: "development", sentryTracesSampleRate: 1 });

    expect(await initTelemetry({ init } as any)).toBe(false);
    expect(init).not.toHaveBeenCalled();
  });

  it("does nothing when the config request fails, and does not throw", async () => {
    const init = vi.fn();
    (globalThis as any).fetch = vi.fn().mockRejectedValue(new Error("offline"));

    await expect(initTelemetry({ init } as any)).resolves.toBe(false);
    expect(init).not.toHaveBeenCalled();
  });

  it("wires both hooks, disables PII and disables session tracking", async () => {
    const init = vi.fn();
    stubConfig({
      sentryDsn: "https://k@errors.example/2",
      sentryEnvironment: "production",
      sentryTracesSampleRate: 1,
    });

    expect(await initTelemetry({ init } as any)).toBe(true);

    const opts = init.mock.calls[0][0];
    expect(opts.dsn).toBe("https://k@errors.example/2");
    expect(opts.sendDefaultPii).toBe(false);
    // GlitchTip does not support sessions.
    expect(opts.autoSessionTracking).toBe(false);
    // Both hooks must be wired - phase 1 shipped a leak because one of a pair was missed.
    expect(opts.beforeSend).toBe(beforeSend);
    expect(opts.beforeBreadcrumb).toBe(beforeBreadcrumb);
    // Tracing arrives in a later release; this one is errors only.
    expect(opts.tracesSampleRate ?? 0).toBe(0);
  });
});
```

Add `vi` to the vitest import at the top of the file.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/lib/telemetry.test.ts`
Expected: FAIL - `beforeSend`, `beforeBreadcrumb`, `initTelemetry` are not exported.

- [ ] **Step 4: Write the minimal implementation**

Append to `apps/web/src/lib/telemetry.ts`:

```ts
import * as Sentry from "@sentry/react";

/** Recursively redact sensitive values. Pure: returns a new structure, never mutates the input. */
function scrub<T>(value: T): T {
  if (Array.isArray(value)) return value.map(scrub) as unknown as T;
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = isSensitiveKey(k) ? REDACTED : scrub(v);
    }
    return out as unknown as T;
  }
  return value;
}

/**
 * Event hook. Redacts by field name AND strips URL query strings, because the two catch different
 * things: the deny-list cannot reach a query string, and URL stripping cannot reach a named field.
 */
export function beforeSend(event: Sentry.ErrorEvent): Sentry.ErrorEvent | null {
  const cleaned = scrub(event);
  if (cleaned.request?.url) cleaned.request.url = stripQueryString(cleaned.request.url);
  return cleaned;
}

/**
 * Breadcrumb hook.
 *
 * Two distinct jobs. First, fetch/xhr breadcrumbs record the request URL, which for the SignalR hub
 * carries `?access_token=<JWT>` - so every breadcrumb URL is stripped.
 *
 * Second, console breadcrumbs capture whatever was logged, which a key-name deny-list cannot vet
 * because the content arrives as a formatted message rather than a named field. Rather than guess at
 * what every console call in the app might contain, low-level console breadcrumbs are dropped
 * entirely; warn and error are kept, since those are the ones with diagnostic value and are written
 * deliberately.
 */
export function beforeBreadcrumb(crumb: Sentry.Breadcrumb): Sentry.Breadcrumb | null {
  if (crumb.category === "console" && !["warning", "error", "fatal"].includes(crumb.level ?? "")) {
    return null;
  }
  const cleaned = scrub(crumb);
  if (typeof cleaned.data?.url === "string") cleaned.data.url = stripQueryString(cleaned.data.url);
  if (typeof cleaned.message === "string") cleaned.message = scrubUrlsIn(cleaned.message);
  return cleaned;
}

/** Injected so tests can assert the options without loading the real SDK. */
interface SentryLike {
  init: (options: Record<string, unknown>) => void;
}

let enabled = false;

/**
 * Fetch the browser DSN from the API and start reporting. Resolves to whether reporting is on.
 *
 * Uses raw fetch rather than the axios client in lib/api: this runs before the app boots, and the
 * axios instance carries auth interceptors (including a 401 -> /login redirect) that have no business
 * firing for a config read.
 *
 * Never throws and never blocks the app: if the config request fails, the SPA boots with telemetry
 * off. The cost of fetching rather than baking the DSN is that errors thrown in the first few
 * milliseconds of boot are missed - accepted, because a baked DSN would force dev and production to
 * share one error-tracking project.
 */
export async function initTelemetry(sdk: SentryLike = Sentry): Promise<boolean> {
  try {
    const res = await fetch("/api/config");
    const cfg = (await res.json()) as { sentryDsn?: string; sentryEnvironment?: string };
    const dsn = (cfg.sentryDsn ?? "").trim();
    if (!dsn) return false;

    sdk.init({
      dsn,
      environment: cfg.sentryEnvironment || "development",
      release: __APP_VERSION__,
      // Never attach request bodies, headers or user identifiers automatically.
      sendDefaultPii: false,
      // GlitchTip does not support sessions.
      autoSessionTracking: false,
      beforeSend,
      beforeBreadcrumb,
    });
    enabled = true;
    return true;
  } catch {
    // Telemetry must never stop the app booting.
    return false;
  }
}

/** Report an error the app caught and handled. A no-op when reporting is off. */
export function captureException(error: unknown, context?: Record<string, unknown>): void {
  if (!enabled) return;
  Sentry.captureException(error, context ? { extra: scrub(context) } : undefined);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/lib/telemetry.test.ts`
Expected: all PASS.

- [ ] **Step 6: Write the failing ErrorBoundary test**

Read `apps/web/src/components/ErrorBoundary.test.tsx` first and follow its existing structure. Append:

```tsx
it("reports the error to telemetry and still renders the fallback", () => {
  const captured: unknown[] = [];
  vi.spyOn(telemetry, "captureException").mockImplementation((e) => void captured.push(e));

  function Boom(): never {
    throw new Error("kaboom");
  }

  render(
    <ErrorBoundary resetKey="/x" message="It broke">
      <Boom />
    </ErrorBoundary>,
  );

  // The user-facing behaviour must be unchanged.
  expect(screen.getByRole("alert")).toHaveTextContent("It broke");
  // And the error is now reported.
  expect(captured).toHaveLength(1);
});
```

Add the imports the file needs (`vi`, and `* as telemetry from "../lib/telemetry"`).

- [ ] **Step 7: Run it to verify it fails**

Run: `cd apps/web && npx vitest run src/components/ErrorBoundary.test.tsx`
Expected: FAIL - `captured` is empty, because nothing reports yet.

- [ ] **Step 8: Wire the ErrorBoundary**

In `apps/web/src/components/ErrorBoundary.tsx`, add `import { captureException } from "../lib/telemetry";` and extend `componentDidCatch`:

```tsx
  componentDidCatch(error: Error, info: unknown) {
    // The error would otherwise vanish with the unmounted tree; log it so it is still diagnosable.
    console.error("Detail panel crashed:", error, info);
    // And report it, so a crash a user hits is visible without them reporting it.
    captureException(error);
  }
```

Do not pass `info` to `captureException` - it is a React component stack, which can contain component names but is already captured by the SDK, and passing it adds an unscrubbed payload for no gain.

- [ ] **Step 9: Wire the boot sequence**

In `apps/web/src/main.tsx`, initialise before rendering. The render must happen whether or not telemetry starts:

```tsx
import { initTelemetry } from "./lib/telemetry";

// Start reporting before the app renders, so a crash during first render is captured. Never blocks:
// initTelemetry resolves false rather than throwing if the config request fails.
void initTelemetry().finally(() => {
  createRoot(document.getElementById("root")!).render(
    // ... the existing tree, unchanged
  );
});
```

Move the existing `createRoot(...).render(...)` call inside the `finally` callback without changing the element tree.

- [ ] **Step 10: Run the full web suite and the build**

```bash
cd apps/web && npx vitest run
```

```bash
cd apps/web && npm run build
```

`npm run build` is `tsc && vite build`, so it gates typecheck as well. Expected: both clean, no warnings.

- [ ] **Step 11: Commit**

```bash
git add apps/web/src/lib/telemetry.ts apps/web/src/lib/telemetry.test.ts apps/web/src/main.tsx apps/web/src/components/ErrorBoundary.tsx apps/web/src/components/ErrorBoundary.test.tsx apps/web/package.json apps/web/package-lock.json
git commit -m "feat: report browser crashes to the error tracker"
```

---

### Task 4: Deploy config, docs and release (PR 4 close-out)

**Files:**
- Modify: `deploy/docker-compose.yml` (api service `environment`)
- Modify: `deploy/.env.example`
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/components/AboutModal.tsx`
- Modify: `version.json` + the four mirrors
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Pass the browser DSN to the API**

Append to the `api` service's `environment:` block in `deploy/docker-compose.yml`:

```yaml
      # Browser DSN, served to the SPA at runtime by GET /api/config. Public by design (it ships in
      # the JS bundle), and a SEPARATE GlitchTip project from Sentry__Dsn so browser noise does not
      # bury server errors. Empty = the SPA reports nothing.
      Sentry__BrowserDsn: ${SENTRY_BROWSER_DSN:-}
```

Verify the key name binds to `TelemetryOptions.BrowserDsn` by reading `src/Diariz.Api/Configuration/AppOptions.cs`. A wrong key means the SPA silently reports nothing with no error anywhere.

- [ ] **Step 2: Add the env var to `deploy/.env.example`**

In the observability block added in phase 1:

```bash
# Browser DSN for the SPA, served at runtime via GET /api/config. Create this as a SEPARATE GlitchTip
# project from the API and worker: browser errors are far noisier (extensions, old tabs, flaky
# networks) and would otherwise bury server-side failures.
SENTRY_BROWSER_DSN=
```

- [ ] **Step 3: Add the About-box disclaimer**

`apps/web/src/components/AboutModal.tsx` lists the bundled open-source libraries. `@sentry/react` now ships in the SPA bundle, so add it to that list. Read the existing paragraph and insert it in keeping with the surrounding wording - do not restructure the list.

This resolves the open question recorded in the design spec's section 13.

- [ ] **Step 4: Update the architecture doc**

Extend the existing `## Observability (optional): GlitchTip` section in `docs/Overall_Synopsis_of_Platform.md` - there must remain exactly one such section. Read it first and check whether any sentence is now stale (previous passes had to correct sentences that said a runtime reported nothing yet).

Cover: the SPA reports browser crashes; the browser DSN is served at runtime from `GET /api/config` and why (one image, many environments); that browser errors go to a separate project; and that the desktop app inherits this automatically because it loads the SPA from the server origin.

Be accurate about the scrubbing: it redacts by field name, strips URL query strings (which carry the SignalR `?access_token=` JWT), and drops low-level console breadcrumbs. Do not claim protections that do not exist.

- [ ] **Step 5: Bump to 0.174.4 and add the release entry**

All five files: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`.

Verify the PR number is free with `gh pr list --state all --limit 1` before writing it.

```ts
{
  version: "0.174.4",
  date: "<today's date, YYYY-MM-DD>",
  pr: 393,
  headline: "Browser crashes are reported automatically",
  summary:
    "When a browser DSN is configured, a crash in the web app is now reported with a stack trace, " +
    "so a problem you hit is visible without you having to report it. Meeting content, credentials " +
    "and access tokens are stripped before anything is sent. This covers the desktop app too, since " +
    "it loads the web app from your server. Deployments without a DSN are unchanged.",
  added: [
    "Optional browser crash reporting for the web and desktop apps.",
  ],
},
```

- [ ] **Step 6: Verify**

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

All must be clean. `versionMirrors` and `releases` are part of the vitest run.

- [ ] **Step 7: Commit, push, open the PR**

```bash
git add deploy/docker-compose.yml deploy/.env.example docs/Overall_Synopsis_of_Platform.md apps/web/src/components/AboutModal.tsx version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "feat: wire browser telemetry into the deployment"
git push -u origin feat/observability-spa
```

PR description states: **server redeploy only, no desktop release** - the Electron shell loads the SPA from the server origin, so installed desktop apps pick this up with no new installer.

**Operational step for the human partner, not part of this PR:** create a third GlitchTip project (`diariz-web`) on each environment and put its DSN in `SENTRY_BROWSER_DSN`.

---

### Task 5: Browser tracing (TDD)

**Files:**
- Modify: `apps/web/src/lib/telemetry.ts`
- Modify: `apps/web/src/lib/telemetry.test.ts`

**Interfaces:**
- Consumes: everything from Tasks 2-3.
- Produces: no new exports. `initTelemetry` gains `integrations` and a non-zero `tracesSampleRate`.

**Prerequisite, and it is a real gate:** the outer proxy must preserve the `sentry-trace` and `baggage` request headers on the app's own hostname. Without them, browser spans and API transactions never join into one trace, and **it fails silently** - you get two disconnected halves and no error. Confirm this before starting; it is recorded as an outstanding item from phase 1's Task 9.

- [ ] **Step 1: Write the failing tests**

Append to `apps/web/src/lib/telemetry.test.ts`:

```ts
describe("initTelemetry with tracing", () => {
  it("enables browser tracing at the configured sample rate", async () => {
    const init = vi.fn();
    stubConfig({
      sentryDsn: "https://k@errors.example/2",
      sentryEnvironment: "production",
      sentryTracesSampleRate: 0.25,
    });

    await initTelemetry({ init } as any);

    const opts = init.mock.calls[0][0];
    expect(opts.tracesSampleRate).toBe(0.25);
    expect(Array.isArray(opts.integrations)).toBe(true);
  });

  it("keeps every scrubbing hook wired once tracing is on", async () => {
    const init = vi.fn();
    stubConfig({ sentryDsn: "https://k@errors.example/2", sentryTracesSampleRate: 1 });

    await initTelemetry({ init } as any);

    const opts = init.mock.calls[0][0];
    expect(opts.beforeSend).toBe(beforeSend);
    expect(opts.beforeBreadcrumb).toBe(beforeBreadcrumb);
    expect(opts.beforeSendTransaction).toBe(beforeSendTransaction);
  });
});

describe("beforeSendTransaction", () => {
  it("strips the access token from the transaction request URL", () => {
    const tx = { request: { url: "/hubs/transcription?access_token=A_LIVE_JWT" } } as any;

    expect(JSON.stringify(beforeSendTransaction(tx))).not.toContain("A_LIVE_JWT");
  });

  it("strips the access token from span descriptions", () => {
    const tx = {
      spans: [{ description: "GET /hubs/transcription?access_token=A_LIVE_JWT" }],
    } as any;

    const cleaned = beforeSendTransaction(tx)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.spans[0].description).toBe("GET /hubs/transcription");
  });

  it("does not throw on a bare transaction", () => {
    expect(() => beforeSendTransaction({} as any)).not.toThrow();
  });
});
```

Import `beforeSendTransaction` at the top of the file.

**Why a separate transaction hook exists:** phase 1 shipped a leak in the .NET API because `beforeSend` does not run for transactions, so every request transaction carried the JWT unscrubbed. Assume the same separation applies here and prove it with these tests rather than trusting that `beforeSend` covers transactions.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/lib/telemetry.test.ts`
Expected: FAIL - `beforeSendTransaction` is not exported and `tracesSampleRate` is 0.

- [ ] **Step 3: Implement**

Add to `apps/web/src/lib/telemetry.ts`:

```ts
/**
 * Transaction hook. Separate from beforeSend because in the Sentry SDKs beforeSend does NOT run for
 * transactions - the .NET API shipped a JWT leak on exactly that gap, found only in a whole-branch
 * review. Transactions fire on every navigation and request, so this is the higher-volume path of the
 * two.
 */
export function beforeSendTransaction(event: Sentry.TransactionEvent): Sentry.TransactionEvent | null {
  const cleaned = scrub(event);
  if (cleaned.request?.url) cleaned.request.url = stripQueryString(cleaned.request.url);
  for (const span of cleaned.spans ?? []) {
    if (typeof span.description === "string") span.description = scrubUrlsIn(span.description);
  }
  return cleaned;
}
```

Then in `initTelemetry`'s `sdk.init({...})`, add:

```ts
      integrations: [Sentry.browserTracingIntegration()],
      tracesSampleRate: cfg.sentryTracesSampleRate ?? 1,
      beforeSendTransaction,
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run`
Expected: all PASS.

- [ ] **Step 5: Verify the SDK's actual transaction behaviour**

The tests above assert this module's own hook. Separately confirm, against the installed `@sentry/react` version, whether `beforeSend` is or is not invoked for transaction events. If it turns out `beforeSend` does cover transactions in this SDK, say so in your report - the hook is still correct and harmless, but the comment explaining it must be accurate. Do not leave a comment asserting something you did not check.

- [ ] **Step 6: Build**

```bash
cd apps/web && npm run build
```

Report the bundle size change from tracing in your report - it is the larger of the two additions.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib/telemetry.ts apps/web/src/lib/telemetry.test.ts
git commit -m "feat: trace browser requests through to the API"
```

---

### Task 6: Tracing docs and release (PR 5 close-out)

**Files:**
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json` + the four mirrors
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Update the architecture doc**

Extend the same single observability section: browser spans now join the API's request transaction into one trace via the `sentry-trace` and `baggage` headers, and **the outer proxy must preserve those headers or tracing silently degrades into disconnected halves**. That caveat is the point of the entry - record it where an operator will find it.

- [ ] **Step 2: Bump to 0.174.5 and add the release entry**

```ts
{
  version: "0.174.5",
  date: "<today's date, YYYY-MM-DD>",
  pr: 394,
  headline: "Page loads and API calls are timed end to end",
  summary:
    "Browser requests are now timed and linked to the matching server request, so a slow page can be " +
    "traced to whichever step actually took the time. Access tokens are stripped from every recorded " +
    "URL. Deployments without a DSN are unchanged.",
  added: [
    "Optional end-to-end request timing from the browser through to the API.",
  ],
},
```

- [ ] **Step 3: Verify**

```bash
cd apps/web && npx vitest run
```

```bash
cd apps/web && npm run build
```

- [ ] **Step 4: Commit, push, open the PR**

```bash
git add docs/Overall_Synopsis_of_Platform.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "feat: document and release browser tracing"
git push
```

PR description: **server redeploy only, no desktop release.** State the outer-proxy header prerequisite explicitly - it is the one thing that will make this look broken if missed.

---

### Task 7: Private source maps (PR 6 - optional)

**Read this before starting.** This task is worth doing but is the fiddliest part of phase 2, and PRs 4-5 stand alone without it. The complication: the deployed SPA is built inside `apps/web/Dockerfile` **on the server**, not in GitHub CI, so an upload step must run where that build happens.

If the upload proves impractical in this deployment, the acceptable fallback is to **stop at Step 1** - ship `sourcemap: "hidden"` and simply not serve the maps. That alone closes the source-disclosure hole. It costs readable production stack traces, which is a real loss, so prefer the full task; but shipping Step 1 alone is strictly better than today.

**Files:**
- Modify: `apps/web/vite.config.ts`
- Modify: `apps/web/Dockerfile`
- Modify: `deploy/.env.example`
- Modify: `docs/Overall_Synopsis_of_Platform.md`

**No unit test.** This is build configuration; nothing here is unit-testable. Verification is by inspecting the built image, per Step 3.

- [ ] **Step 1: Stop serving source maps**

In `apps/web/vite.config.ts`, change `sourcemap: true` to `sourcemap: "hidden"` and replace the existing comment. `"hidden"` still generates the `.map` files but omits the `//# sourceMappingURL=` comment from the bundles, so browsers do not fetch them.

The current comment says shipping maps is "fine for an AGPL/open-source app". Replace it with the actual reason for the change: the maps are now uploaded to the error tracker instead of served, which gives readable stack traces without publishing the source to every visitor.

- [ ] **Step 2: Keep the maps out of the runtime image**

In `apps/web/Dockerfile`, the runtime stage does:

```dockerfile
COPY --from=build /app/dist /usr/share/nginx/html
```

This copies the `.map` files too. Even without a `sourceMappingURL` comment they would remain fetchable by guessing the filename. After that line, delete them:

```dockerfile
# Source maps are uploaded to the error tracker at build time (see the sourcemaps stage), never served:
# without this they stay fetchable by guessing the filename even though nothing links to them.
RUN find /usr/share/nginx/html -name '*.map' -delete
```

- [ ] **Step 3: Verify the maps are genuinely gone**

Build the image and check:

```bash
docker build -f apps/web/Dockerfile -t diariz-web-check apps/web
```

```bash
docker run --rm diariz-web-check find /usr/share/nginx/html -name '*.map'
```

Expected: no output. If any path prints, Step 2 did not work.

- [ ] **Step 4: Upload the maps during the build**

Add an upload step to the build stage in `apps/web/Dockerfile`, gated so a build without credentials still succeeds:

```dockerfile
# Upload source maps to the error tracker so production stack traces are readable, then let the
# runtime stage drop them. Gated on the auth token: a build without one just skips this, so a
# developer build and a CI build both still work.
RUN --mount=type=secret,id=glitchtip_token \
    if [ -f /run/secrets/glitchtip_token ] && [ -n "$GLITCHTIP_URL" ]; then \
      npx --yes glitchtip-cli sourcemaps upload ./dist \
        --url "$GLITCHTIP_URL" \
        --auth-token "$(cat /run/secrets/glitchtip_token)" \
        --release "$APP_VERSION"; \
    else \
      echo "No GlitchTip credentials; skipping source map upload."; \
    fi
```

You will need `ARG GLITCHTIP_URL` and `ARG APP_VERSION` declared in the build stage.

**Use a BuildKit secret, not a build ARG, for the token.** A build ARG is recorded in the image history and readable by anyone who can pull the image.

**Verify the CLI's actual name and flags** before committing this - `npx glitchtip-cli --help`. The flags above are from the design research and may not match the current release. If the CLI differs, use what it actually provides and say so in your report.

**The release value must match** what `initTelemetry` sends as `release` (which is `__APP_VERSION__`), or uploaded maps will not be applied to incoming events. Confirm they agree.

- [ ] **Step 5: Document the build inputs**

Add to `deploy/.env.example`:

```bash
# Source map upload (optional). Without these the web image still builds; production stack traces
# just stay minified. The token is passed as a BuildKit secret, never a build ARG - an ARG is
# recorded in the image history and readable by anyone who can pull the image.
GLITCHTIP_URL=
# Pass at build time, e.g.:
#   GLITCHTIP_TOKEN=... docker compose build --secret id=glitchtip_token,env=GLITCHTIP_TOKEN web
```

- [ ] **Step 6: Update the architecture doc**

Record in the same observability section that the SPA no longer serves source maps, that they are uploaded to GlitchTip at image build time when credentials are supplied, and that the upload is skipped gracefully otherwise.

- [ ] **Step 7: Commit**

```bash
git add apps/web/vite.config.ts apps/web/Dockerfile deploy/.env.example docs/Overall_Synopsis_of_Platform.md
git commit -m "feat: stop serving source maps and upload them instead"
```

---

### Task 8: Source maps release (PR 6 close-out)

**Files:**
- Modify: `version.json` + the four mirrors
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Bump to 0.174.6 and add the release entry**

```ts
{
  version: "0.174.6",
  date: "<today's date, YYYY-MM-DD>",
  pr: 395,
  headline: "Application source is no longer published to visitors",
  summary:
    "The web app previously shipped source maps to every visitor, which made its full source readable " +
    "in the browser. They are now uploaded to the error tracker instead, so crash reports stay just as " +
    "readable for the operator without publishing the source.",
  changed: [
    "Source maps are uploaded to the error tracker at build time rather than served to browsers.",
  ],
},
```

Note this entry uses `changed`, not `added`.

- [ ] **Step 2: Verify**

```bash
cd apps/web && npx vitest run
```

```bash
cd apps/web && npm run build
```

Confirm `dist` contains `.map` files but no bundle references them - `grep -rl sourceMappingURL apps/web/dist` should find nothing in the `.js` files.

- [ ] **Step 3: Commit, push, open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "chore: release private source maps"
git push
```

PR description: **server redeploy only, no desktop release.** State whether the upload step was verified working or only the not-serving half shipped.

---

## Operational steps (human partner, not implementable here)

Carried over and added to phase 1's outstanding list:

1. **Create a third GlitchTip project** (`diariz-web`) per environment and set `SENTRY_BROWSER_DSN`. Keep it separate from the API's: browser errors are far noisier - extensions, stale tabs, flaky mobile networks - and would otherwise bury server failures.
2. **Confirm the outer proxy preserves `sentry-trace` and `baggage`** on the app hostname. Gate on this before PR 5. It fails silently.
3. **Measure ad-blocker loss** after PR 4 has run for a few days. Blockers pattern-match Sentry ingest paths, so some proportion of browser events never arrive, with no signal. If material, the mitigation is a same-origin tunnel through `apps/web/nginx.conf` - not planned here, because the size of the problem is unknown until measured.
4. **The event-inspection gate, again.** Before enabling the browser DSN in production, capture a real browser error on dev and confirm no transcript text, no `access_token`, and no voiceprint appears anywhere in the payload. Phase 1 found four separate leaks this way; the browser is the runtime with the most default-on capture channels.

## Self-review notes

**Spec coverage.** Design spec section 6 maps as: 6.1 sub-phase 2a and the DSN delivery problem (Tasks 1-4), 6.2 sub-phase 2b browser tracing (Tasks 5-6), 6.3 sub-phase 2c source maps (Tasks 7-8). Section 7.1's off-by-default is Task 3; 8.5's ad-blocker question is an operational measurement, deliberately not a code task. Section 13's open question about the About-box disclaimer is resolved in Task 4 Step 3 (yes - the modal enumerates bundled libraries, and `@sentry/react` becomes one).

**Deliberately out of scope:** `@sentry/electron` for Electron main-process errors (the renderer is covered because it loads the SPA); session replay (GlitchTip does not support it); API-to-worker trace linking across the Redis stream (still deferred from phase 1); and the parked phase 1 gap that no test pins `Program.cs`'s hook wiring - Task 1 and Task 3 pin the new hooks they add, but the existing three remain unpinned.

**Deferred pins.** `@sentry/react`'s version is resolved at implementation time (Task 3 Step 1), as is the GlitchTip CLI's exact flag set (Task 7 Step 4), which must be checked against `--help` rather than trusted from this document.
