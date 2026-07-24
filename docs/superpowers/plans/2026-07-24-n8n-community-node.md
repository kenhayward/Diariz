# Diariz n8n Community Node Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `n8n-nodes-diariz`, a verified n8n community node package giving full REST and event coverage of Diariz, preceded by four new AI-output webhook events.

**Architecture:** Phase 0 extends the existing webhook event catalogue in the .NET API (four new types emitted from the four AI processors, following the `FormulaRunProcessor` precedent exactly). Phases 1-5 build a dependency-free TypeScript package at `integrations/n8n-nodes-diariz/`: a self-registering webhook Trigger node that verifies Standard Webhooks HMAC signatures, and an Action node combining ten hand-authored resources with a tail generated from the platform's own OpenAPI document, guarded against drift by CI.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core for Phase 0. TypeScript 5 + `n8n-workflow` types, Node's built-in `crypto`, `node --test` (matching `apps/desktop`), `eslint-plugin-n8n-nodes-base`. **Zero runtime dependencies.**

**Design spec:** `docs/superpowers/specs/2026-07-24-n8n-community-node-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail, then write the minimal code. No production code without a preceding failing test.
- **Test output must be pristine** - a passing run has no errors or warnings.
- **No em/en dashes in user-facing text.** Use a plain hyphen `-`. This binds every n8n `displayName`, `description`, `subtitle`, error message and README line, and every string in `apps/web/src/locales/**`. Code comments and internal docs are exempt.
- **`main` is branch-protected.** Every phase lands as its own PR (`git push -u origin <branch>` + `gh pr create`). Never commit or merge to `main` locally.
- **Zero runtime dependencies** in `integrations/n8n-nodes-diariz/package.json`. Only `devDependencies`. This is an n8n verification requirement and is not negotiable; HTTP goes through `this.helpers.httpRequestWithAuthentication` and hashing through `node:crypto`.
- **Node package licence is MIT**, in its own `LICENSE` file inside `integrations/n8n-nodes-diariz/`. The repository root stays AGPL-3.0. Do not change the root `LICENSE`.
- **The npm package version is independent** of `/version.json`. It is NOT a lockstep mirror. Platform releases still follow the normal checklist.
- **Webhook event type keys are append-only.** Never renumber, rename or remove an existing key - subscriptions store them as a CSV string.
- **`RecordingStatus` and `RecordingSource` ints are append-only** (Postgres-persisted enums).
- Package name `n8n-nodes-diariz`; node type names `diariz` and `diarizTrigger`; credential name `diarizApi`. These appear in published workflows and cannot change after release.

---

# Phase 0: AI-output webhook events (API)

**Branch:** `feat/webhook-ai-output-events`
**Deployment surface:** Server redeploy. No desktop release. No migration (event keys live in a CSV string column, no schema change).
**Release:** functional enhancement -> Minor bump.

### Task 0.1: Add the four event type keys

**Files:**
- Modify: `src/Diariz.Api/Webhooks/WebhookEventTypes.cs`
- Test: `tests/Diariz.Api.Tests/WebhookEventTypesTests.cs` (create if absent; check first)

**Interfaces:**
- Consumes: nothing.
- Produces: `WebhookEventTypes.RecordingSummarized`, `.RecordingMinutesReady`, `.RecordingActionItemsReady`, `.RecordingTagsReady` (all `const string`), and a `Subscribable` list of **nine** entries.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.Tests/WebhookEventTypesTests.cs`:

```csharp
[Fact]
public void Subscribable_ContainsTheAiOutputEvents()
{
    Assert.Contains("recording.summarized", WebhookEventTypes.Subscribable);
    Assert.Contains("recording.minutes_ready", WebhookEventTypes.Subscribable);
    Assert.Contains("recording.action_items_ready", WebhookEventTypes.Subscribable);
    Assert.Contains("recording.tags_ready", WebhookEventTypes.Subscribable);
}

[Fact]
public void Subscribable_ExcludesPingAndHasNoDuplicates()
{
    Assert.DoesNotContain(WebhookEventTypes.Ping, WebhookEventTypes.Subscribable);
    Assert.Equal(WebhookEventTypes.Subscribable.Count, WebhookEventTypes.Subscribable.Distinct().Count());
    Assert.Equal(9, WebhookEventTypes.Subscribable.Count);
}
```

If the file does not exist, create it with `namespace Diariz.Api.Tests;`, `using Diariz.Api.Webhooks;`, and `public class WebhookEventTypesTests { ... }`.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookEventTypesTests"
```

Expected: FAIL - `recording.summarized` not found in Subscribable.

- [ ] **Step 3: Write minimal implementation**

In `src/Diariz.Api/Webhooks/WebhookEventTypes.cs`, add the constants after `RecordingTranscriptionFailed`:

```csharp
    public const string RecordingSummarized = "recording.summarized";
    public const string RecordingMinutesReady = "recording.minutes_ready";
    public const string RecordingActionItemsReady = "recording.action_items_ready";
    public const string RecordingTagsReady = "recording.tags_ready";
```

and extend `Subscribable`:

```csharp
    public static readonly IReadOnlyList<string> Subscribable = new[]
    {
        RecordingCreated, RecordingTranscribed, RecordingTranscriptionFailed,
        RecordingSummarized, RecordingMinutesReady, RecordingActionItemsReady, RecordingTagsReady,
        FormulaResultCompleted, FormulaResultFailed,
    };
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookEventTypesTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Webhooks/WebhookEventTypes.cs tests/Diariz.Api.Tests/WebhookEventTypesTests.cs
git commit -m "feat: add AI-output webhook event types"
```

---

### Task 0.2: Emit `recording.summarized`

Establishes the emission pattern the next task repeats three times. Copy `FormulaRunProcessor` / `FormulaRunWorker`, which already do exactly this.

**Files:**
- Modify: `src/Diariz.Api/Services/SummarizationProcessor.cs`
- Modify: `src/Diariz.Api/Services/SummarizationWorker.cs`
- Test: `tests/Diariz.Api.Tests/SummarizationProcessorTests.cs` (extend; check the existing file name first with `ls tests/Diariz.Api.Tests | grep -i summar`)

**Interfaces:**
- Consumes: `WebhookEventTypes.RecordingSummarized` (Task 0.1); `IWebhookPublisher.PublishAsync(eventType, ownerUserId, data, signals, platformData, ct)`; `WebhookPayload.For(publicUrl, recordingId)`.
- Produces: `SummarizationProcessor.ProcessAsync` gains two trailing-but-before-`ct` parameters `IWebhookPublisher webhooks, string publicUrl`. Task 0.3 mirrors this signature change in three other processors.

- [ ] **Step 1: Write the failing test**

There is an existing fake publisher used by the formula tests. Find it first:

```bash
grep -rn "class FakeWebhookPublisher" tests/
```

Use that type. If it does not exist, create it in `tests/Diariz.Api.TestSupport/Fakes.cs` (the shared fakes home - no mocking library in this repo):

```csharp
public sealed class FakeWebhookPublisher : IWebhookPublisher
{
    public List<(string Type, Guid UserId, object Data)> Published { get; } = new();

    public Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)
    {
        Published.Add((eventType, ownerUserId, data));
        return Task.CompletedTask;
    }
}
```

Add to the summarization processor tests:

```csharp
[Fact]
public async Task ProcessAsync_OnSuccess_PublishesSummarizedEvent()
{
    using var db = TestDb.Create();
    var (rec, transcription) = SeedRecordingWithSegments(db);   // reuse this test class's existing seed helper
    var webhooks = new FakeWebhookPublisher();

    await SummarizationProcessor.ProcessAsync(
        db, new FakeSummarizationClient("A summary.", "A name"), new FakeSettingsResolver(),
        new FakeHubContext(), new SummarizationJob(rec.Id, transcription.Id), "template",
        NullLogger.Instance, webhooks, "https://diariz.example.com");

    var ev = Assert.Single(webhooks.Published);
    Assert.Equal("recording.summarized", ev.Type);
    Assert.Equal(rec.UserId, ev.UserId);
}

[Fact]
public async Task ProcessAsync_WhenSummarizationFails_PublishesNothing()
{
    using var db = TestDb.Create();
    var (rec, transcription) = SeedRecordingWithSegments(db);
    var webhooks = new FakeWebhookPublisher();

    await SummarizationProcessor.ProcessAsync(
        db, new ThrowingSummarizationClient(), new FakeSettingsResolver(),
        new FakeHubContext(), new SummarizationJob(rec.Id, transcription.Id), "template",
        NullLogger.Instance, webhooks, "https://diariz.example.com");

    Assert.Empty(webhooks.Published);
}
```

Match the existing test class's helper and fake names exactly - read the file before writing. If `ThrowingSummarizationClient` does not exist, add a fake whose `SummarizeAsync` throws `new InvalidOperationException("boom")`.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationProcessorTests"
```

Expected: FAIL to compile - `ProcessAsync` does not take 9 arguments.

- [ ] **Step 3: Write minimal implementation**

In `src/Diariz.Api/Services/SummarizationProcessor.cs`, change the signature:

```csharp
    public static async Task ProcessAsync(
        DiarizDbContext db, ISummarizationClient client, ISummarizationSettingsResolver resolver,
        IHubContext<TranscriptionHub> hub, SummarizationJob job, string template, ILogger logger,
        IWebhookPublisher webhooks, string publicUrl,
        CancellationToken ct = default)
```

Add `using Diariz.Api.Webhooks;` at the top. Then, immediately after **each** of the two success-path `await hub.NotifyStatusAsync(rec.UserId, rec.Id, RecordingStatus.Summarized.ToString());` calls (there are two - the hand-edited short-circuit near the top and the normal completion), insert:

```csharp
            await PublishSummarizedAsync(db, webhooks, publicUrl, rec, ct);
```

For the short-circuit case, `return;` follows as before. Add the helper at the bottom of the class:

```csharp
    /// <summary>Best-effort event emission. Both success paths emit: a preserved hand-edited summary still
    /// means the recording reached Summarized, and a subscriber waiting on "summary ready" must not hang.</summary>
    private static async Task PublishSummarizedAsync(
        DiarizDbContext db, IWebhookPublisher webhooks, string publicUrl, Recording rec, CancellationToken ct)
    {
        var summaryText = await db.Summaries
            .Where(s => s.Transcription!.RecordingId == rec.Id)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Text)
            .FirstOrDefaultAsync(ct);

        await webhooks.PublishAsync(WebhookEventTypes.RecordingSummarized, rec.UserId, new
        {
            recordingId = rec.Id,
            name = rec.Name ?? rec.Title,
            status = rec.Status.ToString(),
            summary = summaryText,
            links = WebhookPayload.For(publicUrl, rec.Id),
        }, ct: ct);
    }
```

Note the `summary` field: this event exists so a workflow can act on the summary text, and requiring a second REST call to fetch it would defeat the purpose. Confirm the `Summary` -> `Transcription` navigation name by reading `src/Diariz.Domain/Entities/`; if `Summary` has no `Transcription` navigation, query via `db.Transcriptions.Where(t => t.RecordingId == rec.Id).OrderByDescending(t => t.Version).Select(t => t.Summary!.Text)` instead.

In `src/Diariz.Api/Services/SummarizationWorker.cs`, mirror `FormulaRunWorker`: add a `_publicUrl` field set from `appOpts.Value.PublicUrl` in the constructor (inject `IOptions<AppOptions>` - check `FormulaRunWorker.cs:33` for the exact options type name), resolve the publisher inside the existing DI scope, and pass both through:

```csharp
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
                await SummarizationProcessor.ProcessAsync(
                    ctx, client, resolver, _hub, job, template, _log, webhooks, _publicUrl, ct);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationProcessorTests"
```

Expected: PASS.

- [ ] **Step 5: Build the whole solution**

Integration tests construct these types too, and a unit-only run will not catch a break there.

```bash
dotnet build Diariz.slnx
```

Expected: Build succeeded, 0 errors, 0 warnings. Fix any second construction site the compiler names.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/SummarizationProcessor.cs src/Diariz.Api/Services/SummarizationWorker.cs tests/
git commit -m "feat: emit recording.summarized webhook event"
```

---

### Task 0.3: Emit `minutes_ready`, `action_items_ready` and `tags_ready`

Applies Task 0.2's pattern to the three remaining processors. Each gets the same
`IWebhookPublisher webhooks, string publicUrl` parameter pair before `ct`, the same
best-effort publish immediately after the success-path `NotifyStatusAsync`, and the same
worker wiring.

**Files:**
- Modify: `src/Diariz.Api/Services/MeetingMinutesProcessor.cs`, `src/Diariz.Api/Services/MeetingMinutesWorker.cs`
- Modify: `src/Diariz.Api/Services/ActionsProcessor.cs`, `src/Diariz.Api/Services/ActionsWorker.cs`
- Modify: `src/Diariz.Api/Services/TagsProcessor.cs`, `src/Diariz.Api/Services/TagsWorker.cs`
- Test: the matching `tests/Diariz.Api.Tests/*ProcessorTests.cs` files

**Interfaces:**
- Consumes: Task 0.1's constants; `FakeWebhookPublisher` from Task 0.2.
- Produces: nothing new for later tasks beyond the events themselves.

Payload shapes (each also carries `links = WebhookPayload.For(publicUrl, rec.Id)`):

| Event | Fields beyond `recordingId`, `name`, `status`, `links` |
|---|---|
| `recording.minutes_ready` | `minutes` (the generated Markdown), `meetingTypeId`, `meetingTypeName` |
| `recording.action_items_ready` | `actionItems` (array of `{ id, text, assignee, dueDate, completed }`), `count` |
| `recording.tags_ready` | `tags` (array of `{ name, weight }`), `count` |

Read each processor before writing to confirm the entity property names for these
projections; do not guess at `Action` or `Tag` column names.

- [ ] **Step 1: Write the failing tests**

For each of the three, add the two tests from Task 0.2 Step 1, adapted:

```csharp
[Fact]
public async Task ProcessAsync_OnSuccess_PublishesMinutesReadyEvent()
{
    using var db = TestDb.Create();
    var (rec, transcription) = SeedRecordingWithSegments(db);
    var webhooks = new FakeWebhookPublisher();

    await MeetingMinutesProcessor.ProcessAsync(
        db, new FakeMinutesGenerator("## Minutes"), new FakeSettingsResolver(), new FakeHubContext(),
        new FakeJobQueue(), new MeetingMinutesJob(rec.Id, transcription.Id), 10000, NullLogger.Instance,
        webhooks, "https://diariz.example.com");

    var ev = Assert.Single(webhooks.Published);
    Assert.Equal("recording.minutes_ready", ev.Type);
}

[Fact]
public async Task ProcessAsync_WhenGenerationFails_PublishesNothing()
{
    // ... same shape, throwing fake, Assert.Empty(webhooks.Published);
}
```

Use each test class's real existing fakes and job-record constructors - read the file first.
Repeat for `ActionsProcessor` (`recording.action_items_ready`) and `TagsProcessor`
(`recording.tags_ready`).

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingMinutesProcessorTests|FullyQualifiedName~ActionsProcessorTests|FullyQualifiedName~TagsProcessorTests"
```

Expected: FAIL to compile - wrong argument count on all three.

- [ ] **Step 3: Write the implementations**

For each processor, following Task 0.2 exactly:

1. Add `using Diariz.Api.Webhooks;`.
2. Insert `IWebhookPublisher webhooks, string publicUrl,` immediately before `CancellationToken ct = default` in `ProcessAsync`.
3. After the success-path `await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());`, call a private `PublishXAsync` helper built like Task 0.2's, using the payload table above.
4. Wrap nothing in try/catch - `WebhookPublisher.PublishAsync` is documented as best-effort and never throws.
5. In the matching worker, add `_publicUrl` from `AppOptions`, resolve `IWebhookPublisher` in the existing DI scope, and pass both to `ProcessAsync`.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS, no warnings.

- [ ] **Step 5: Build the solution**

```bash
dotnet build Diariz.slnx
```

Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services tests/
git commit -m "feat: emit minutes, action-items and tags webhook events"
```

---

### Task 0.4: Surface the new events in the Automations UI

The create/edit Automation form lists subscribable events from a hard-coded array in the web
app; four new server-side keys are invisible until they are added there and translated.

**Files:**
- Modify: `apps/web/src/components/**` - find with `grep -rln "recording.transcription_failed" apps/web/src`
- Modify: `apps/web/src/locales/en/*.json`, `.../de/*.json`, `.../es/*.json`, `.../fr/*.json` (find the existing event labels the same way)
- Test: the existing Automations component test - find with `grep -rln "Automation" apps/web/src --include=*.test.tsx`

**Interfaces:**
- Consumes: the nine keys from Task 0.1.
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

In the Automations component test:

```tsx
it("offers the AI output events when creating an automation", async () => {
  renderAutomations();                     // reuse this file's existing render helper
  await screen.findByText(/Recording Transcribed/i);

  expect(screen.getByText(/Summary Ready/i)).toBeInTheDocument();
  expect(screen.getByText(/Meeting Minutes Ready/i)).toBeInTheDocument();
  expect(screen.getByText(/Action Items Ready/i)).toBeInTheDocument();
  expect(screen.getByText(/Tags Ready/i)).toBeInTheDocument();
});
```

Follow the established web-test pattern: `vi.mock` of `../lib/api`, render inside
`MemoryRouter` + `QueryClientProvider` + `SelectionProvider` (see
`apps/web/src/components/RecordingsPanel.test.tsx`).

- [ ] **Step 2: Run test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/<TheFile>.test.tsx
```

Expected: FAIL - unable to find text "Summary Ready".

- [ ] **Step 3: Add the four options and translations**

Extend the event array with the four keys, each pointing at a new i18n key. Add to all four
locale catalogues (plain hyphens only, no em dashes):

```json
"webhookEvents": {
  "recording.summarized": "Summary Ready",
  "recording.minutes_ready": "Meeting Minutes Ready",
  "recording.action_items_ready": "Action Items Ready",
  "recording.tags_ready": "Tags Ready"
}
```

German: "Zusammenfassung bereit", "Protokoll bereit", "Aufgaben bereit", "Tags bereit".
Spanish: "Resumen listo", "Acta lista", "Tareas listas", "Etiquetas listas".
French: "Resume pret", "Compte rendu pret", "Taches pretes", "Etiquettes pretes".
Match the surrounding catalogue's accent conventions - read the neighbouring French and
Spanish strings and follow them.

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd apps/web && npm test
```

Expected: PASS, no warnings.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src
git commit -m "feat: offer AI output events in the Automations picker"
```

---

### Task 0.5: Phase 0 release and docs

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Read the current version**

```bash
cat version.json
```

- [ ] **Step 2: Bump Minor, reset Build**

A functional enhancement, so `0.158.0` -> `0.159.0` (use whatever the actual current value is).
Update `version.json` and all three mirrors to the same value.

- [ ] **Step 3: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`, with `version` equal to `version.json`,
today's `date`, the real `pr` number, a `headline`, a prose `summary`, and an `added` list
naming the four events. No em/en dashes.

- [ ] **Step 4: Update the feature docs in lockstep**

The Automations row in the README Features table, the matching `docs/features.md` bullet, and
the `CAPABILITIES` row in `releases.ts` all describe the event catalogue - update all three to
say nine events including the AI outputs. Update the webhook event list in
`docs/Overall_Synopsis_of_Platform.md`. No `Data_Schema.md` change (no schema change).

- [ ] **Step 5: Verify the release assertion**

```bash
cd apps/web && npx vitest run src/lib/releases.test.ts
```

Expected: PASS (`RELEASES[0].version` equals `version.json`).

- [ ] **Step 6: Full test sweep**

```bash
dotnet test tests/Diariz.Api.Tests
```
```bash
cd apps/web && npm test
```

Expected: both green, no warnings.

- [ ] **Step 7: Commit, push, open the PR**

```bash
git add -A && git commit -m "chore: release 0.159.0 - AI output webhook events"
```
```bash
git push -u origin feat/webhook-ai-output-events
```

Open the PR with `gh pr create`, stating in the body: **server redeploy only, no desktop
release**, no migration, and that it precedes the n8n community node work.

---

# Phase 1: Package scaffold and credential

**Branch:** `feat/n8n-node-scaffold`
**Deployment surface:** New npm package directory only. Neither a server redeploy nor a desktop release.

### Task 1.1: Scaffold the package

**Files:**
- Create: `integrations/n8n-nodes-diariz/package.json`, `tsconfig.json`, `.eslintrc.js`, `gulpfile.js`, `LICENSE`, `.npmignore`, `README.md`, `CHANGELOG.md`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/diariz.svg`

**Interfaces:**
- Produces: `npm run build` compiles `nodes/` and `credentials/` to `dist/`; `npm test` runs `node --test`; `npm run lint` runs the n8n linter. Every later task depends on these three scripts.

- [ ] **Step 1: Write the failing test**

Create `integrations/n8n-nodes-diariz/test/package.test.ts`:

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import pkg from "../package.json" with { type: "json" };

test("declares no runtime dependencies (n8n verification requirement)", () => {
  assert.deepEqual(pkg.dependencies ?? {}, {});
});

test("is discoverable as an n8n community node package", () => {
  assert.equal(pkg.name, "n8n-nodes-diariz");
  assert.equal(pkg.license, "MIT");
  assert.ok(pkg.keywords.includes("n8n-community-node-package"));
  assert.equal(pkg.n8n.n8nNodesApiVersion, 1);
});

test("registers both nodes and the credential", () => {
  assert.deepEqual(pkg.n8n.credentials, ["dist/credentials/DiarizApi.credentials.js"]);
  assert.deepEqual(pkg.n8n.nodes, [
    "dist/nodes/Diariz/Diariz.node.js",
    "dist/nodes/Diariz/DiarizTrigger.node.js",
  ]);
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - no `package.json`, no script.

- [ ] **Step 3: Write the package files**

`integrations/n8n-nodes-diariz/package.json`:

```json
{
  "name": "n8n-nodes-diariz",
  "version": "0.1.0",
  "description": "n8n community node for Diariz meeting transcription: react to transcriptions and drive the full REST API.",
  "keywords": ["n8n-community-node-package", "n8n", "diariz", "transcription", "meetings"],
  "license": "MIT",
  "homepage": "https://github.com/kenhayward/Diariz/tree/main/integrations/n8n-nodes-diariz",
  "repository": { "type": "git", "url": "git+https://github.com/kenhayward/Diariz.git", "directory": "integrations/n8n-nodes-diariz" },
  "main": "index.js",
  "engines": { "node": ">=20.15" },
  "scripts": {
    "build": "tsc && gulp build:icons",
    "dev": "tsc --watch",
    "lint": "eslint nodes credentials package.json",
    "lintfix": "eslint nodes credentials package.json --fix",
    "test": "tsc --noEmit -p tsconfig.test.json && node --test --experimental-strip-types test/*.test.ts",
    "generate": "node --experimental-strip-types scripts/generate.ts",
    "prepublishOnly": "npm run build && npm run lint && npm test"
  },
  "files": ["dist"],
  "n8n": {
    "n8nNodesApiVersion": 1,
    "credentials": ["dist/credentials/DiarizApi.credentials.js"],
    "nodes": ["dist/nodes/Diariz/Diariz.node.js", "dist/nodes/Diariz/DiarizTrigger.node.js"]
  },
  "devDependencies": {
    "@typescript-eslint/parser": "^8.0.0",
    "eslint": "^8.57.0",
    "eslint-plugin-n8n-nodes-base": "^1.16.3",
    "gulp": "^5.0.0",
    "n8n-workflow": "*",
    "typescript": "^5.6.0"
  },
  "peerDependencies": { "n8n-workflow": "*" }
}
```

`tsconfig.json`:

```json
{
  "compilerOptions": {
    "strict": true,
    "module": "commonjs",
    "moduleResolution": "node",
    "target": "es2022",
    "lib": ["es2022"],
    "resolveJsonModule": true,
    "esModuleInterop": true,
    "declaration": true,
    "outDir": "dist",
    "rootDir": ".",
    "noUnusedLocals": true,
    "useUnknownInCatchVariables": false
  },
  "include": ["credentials/**/*", "nodes/**/*"],
  "exclude": ["dist", "node_modules", "test", "scripts"]
}
```

`tsconfig.test.json` extends it with `"include": ["test/**/*", "scripts/**/*", "nodes/**/*", "credentials/**/*"]` and `"noEmit": true`.

`.eslintrc.js`:

```js
module.exports = {
  root: true,
  parser: "@typescript-eslint/parser",
  parserOptions: { ecmaVersion: 2022, sourceType: "module" },
  ignorePatterns: [".eslintrc.js", "dist/**", "gulpfile.js"],
  overrides: [
    {
      files: ["package.json"],
      plugins: ["n8n-nodes-base"],
      extends: ["plugin:n8n-nodes-base/community"],
      rules: { "n8n-nodes-base/community-package-json-name-still-default": "off" },
    },
    { files: ["./credentials/**/*.ts"], plugins: ["n8n-nodes-base"], extends: ["plugin:n8n-nodes-base/credentials"] },
    { files: ["./nodes/**/*.ts"], plugins: ["n8n-nodes-base"], extends: ["plugin:n8n-nodes-base/nodes"] },
  ],
};
```

`gulpfile.js`:

```js
const { src, dest } = require("gulp");

function buildIcons() {
  return src("nodes/**/*.{png,svg}").pipe(dest("dist/nodes"));
}

exports["build:icons"] = buildIcons;
```

`LICENSE`: the standard MIT text, `Copyright (c) 2026 Ken Hayward`. `.npmignore`: exclude
`test`, `scripts`, `tsconfig*.json`, `.eslintrc.js`, `gulpfile.js`.

`nodes/Diariz/diariz.svg`: a simple square-viewBox microphone glyph, single path, no external
references. `apps/desktop/build/make-tray-icon.js` already draws a microphone - reuse its path
data for visual consistency.

- [ ] **Step 4: Install and run the tests**

```bash
cd integrations/n8n-nodes-diariz && npm install && npm test
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Verify the lint and build gates work**

```bash
cd integrations/n8n-nodes-diariz && npm run lint && npm run build
```

Expected: both succeed (build produces an empty-ish `dist` at this point; that is fine).

- [ ] **Step 6: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "chore: scaffold the n8n-nodes-diariz package"
```

---

### Task 1.2: The Diariz API credential

**Files:**
- Create: `integrations/n8n-nodes-diariz/credentials/DiarizApi.credentials.ts`
- Test: `integrations/n8n-nodes-diariz/test/credential.test.ts`

**Interfaces:**
- Produces: credential name `diarizApi` with properties `baseUrl` and `apiToken`. Every node reads them via `this.getCredentials("diarizApi")`; the base URL is always used trimmed of any trailing slash.

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { DiarizApi } from "../credentials/DiarizApi.credentials";

test("authenticates with a bearer token", () => {
  const c = new DiarizApi();
  assert.equal(c.name, "diarizApi");
  assert.equal(c.authenticate.properties.headers!.Authorization,
    "=Bearer {{$credentials.apiToken}}");
});

test("tests the credential against the profile endpoint", () => {
  const c = new DiarizApi();
  assert.equal(c.test.request.url, "/api/user/profile");
  assert.equal(c.test.request.baseURL, "={{$credentials.baseUrl.replace(/\\/$/, '')}}");
});

test("warns when the platform has API access or automations turned off", () => {
  const c = new DiarizApi();
  const keys = c.test.rules!.map((r: any) => r.properties.key);
  assert.ok(keys.includes("apiAccessEnabled"));
  assert.ok(keys.includes("webhooksEnabled"));
});

test("keeps the API token out of logs", () => {
  const c = new DiarizApi();
  const token = c.properties.find((p) => p.name === "apiToken")!;
  assert.equal(token.typeOptions?.password, true);
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - cannot find module `../credentials/DiarizApi.credentials`.

- [ ] **Step 3: Write the credential**

```ts
import type {
  IAuthenticateGeneric,
  ICredentialTestRequest,
  ICredentialType,
  INodeProperties,
} from "n8n-workflow";

export class DiarizApi implements ICredentialType {
  name = "diarizApi";
  displayName = "Diariz API";
  documentationUrl = "https://github.com/kenhayward/Diariz/tree/main/integrations/n8n-nodes-diariz";

  properties: INodeProperties[] = [
    {
      displayName: "Base URL",
      name: "baseUrl",
      type: "string",
      default: "",
      required: true,
      placeholder: "https://diariz.example.com",
      description: "The address of your Diariz server, without a trailing path",
    },
    {
      displayName: "API Token",
      name: "apiToken",
      type: "string",
      typeOptions: { password: true },
      default: "",
      required: true,
      description:
        "A personal API token (dz_api_...) from Settings > Developers. A read-only token blocks every write operation; a token with an expiry stops working on that date.",
    },
  ];

  authenticate: IAuthenticateGeneric = {
    type: "generic",
    properties: { headers: { Authorization: "=Bearer {{$credentials.apiToken}}" } },
  };

  test: ICredentialTestRequest = {
    request: {
      baseURL: "={{$credentials.baseUrl.replace(/\\/$/, '')}}",
      url: "/api/user/profile",
    },
    rules: [
      {
        type: "responseSuccessBody",
        properties: {
          key: "apiAccessEnabled",
          value: false,
          message:
            "Your token is valid, but API access is turned off on this Diariz instance. Ask your platform administrator to enable it in Settings.",
        },
      },
      {
        type: "responseSuccessBody",
        properties: {
          key: "webhooksEnabled",
          value: false,
          message:
            "Your token works and action nodes will run, but Automations are turned off, so the Diariz Trigger cannot activate. Ask your platform administrator to enable Automations.",
        },
      },
    ],
  };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint
```

Expected: PASS and lint clean.

- [ ] **Step 5: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add the Diariz API credential"
```

---

### Task 1.3: HTTP transport and error mapping

**Files:**
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/transport/request.ts`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/transport/pagination.ts`
- Test: `integrations/n8n-nodes-diariz/test/transport.test.ts`

**Interfaces:**
- Produces:
  - `diarizApiRequest(this: IExecuteFunctions | ILoadOptionsFunctions | IHookFunctions, method: IHttpRequestMethods, path: string, body?: IDataObject, qs?: IDataObject, options?: Partial<IHttpRequestOptions>): Promise<any>`
  - `describeError(status: number, body: unknown): string` - pure, unit-tested.
  - `applyLimit<T>(items: T[], returnAll: boolean, limit: number): T[]` - pure.
- Every later task's operations go through `diarizApiRequest`; nothing calls `httpRequest` directly.

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { describeError } from "../nodes/Diariz/transport/request";
import { applyLimit } from "../nodes/Diariz/transport/pagination";

test("explains an expired or revoked token", () => {
  assert.match(describeError(401, {}), /expired or been revoked/i);
});

test("explains a disabled platform capability", () => {
  assert.match(describeError(403, {}), /administrator/i);
});

test("passes a server message through when there is one", () => {
  assert.match(describeError(400, "Automation limit reached. Delete one before adding another."),
    /Automation limit reached/);
});

test("prefers a problem-details title over a bare status", () => {
  assert.match(describeError(400, { title: "That key is already in use." }), /already in use/);
});

test("returns everything when returnAll is set", () => {
  assert.equal(applyLimit([1, 2, 3], true, 2).length, 3);
});

test("truncates to the limit otherwise", () => {
  assert.deepEqual(applyLimit([1, 2, 3], false, 2), [1, 2]);
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - modules not found.

- [ ] **Step 3: Write the transport**

`transport/pagination.ts`:

```ts
/** Diariz list endpoints return the full collection, so Return All / Limit is applied here.
 *  See the design spec section 12 finding 1 - this becomes real paging if the API gains skip/take. */
export function applyLimit<T>(items: T[], returnAll: boolean, limit: number): T[] {
  return returnAll ? items : items.slice(0, limit);
}
```

`transport/request.ts`:

```ts
import type {
  IDataObject,
  IExecuteFunctions,
  IHookFunctions,
  IHttpRequestMethods,
  IHttpRequestOptions,
  ILoadOptionsFunctions,
} from "n8n-workflow";
import { NodeApiError } from "n8n-workflow";

type Ctx = IExecuteFunctions | ILoadOptionsFunctions | IHookFunctions;

/** Turns a Diariz HTTP failure into a sentence a workflow author can act on. Pure, so it is unit-tested. */
export function describeError(status: number, body: unknown): string {
  const fromBody =
    typeof body === "string" && body.trim().length > 0
      ? body.trim()
      : typeof body === "object" && body !== null
        ? ((body as IDataObject).title as string) ?? ((body as IDataObject).message as string) ?? ""
        : "";

  if (status === 401) {
    return "Diariz rejected the token. It may have expired or been revoked - check Settings > Developers, and remember that tokens can carry an expiry date.";
  }
  if (status === 403) {
    return fromBody ||
      "Diariz refused this request. The capability may be turned off by your platform administrator, or your token may be read-only.";
  }
  if (status === 404) {
    return fromBody || "Diariz could not find that item. It may have been deleted, or it may belong to another account.";
  }
  return fromBody || `Diariz returned HTTP ${status}.`;
}

export async function diarizApiRequest(
  this: Ctx,
  method: IHttpRequestMethods,
  path: string,
  body?: IDataObject,
  qs?: IDataObject,
  options: Partial<IHttpRequestOptions> = {},
): Promise<any> {
  const { baseUrl } = (await this.getCredentials("diarizApi")) as { baseUrl: string };

  const request: IHttpRequestOptions = {
    method,
    url: `${baseUrl.replace(/\/$/, "")}${path}`,
    json: true,
    ...options,
  };
  if (body !== undefined) request.body = body;
  if (qs !== undefined) request.qs = qs;

  try {
    return await this.helpers.httpRequestWithAuthentication.call(this, "diarizApi", request);
  } catch (error: any) {
    const status = error?.httpCode ? Number(error.httpCode) : (error?.statusCode ?? 0);
    throw new NodeApiError(this.getNode(), error, {
      message: describeError(status, error?.response?.body ?? error?.error),
      httpCode: String(status),
    });
  }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint
```

Expected: PASS, lint clean.

- [ ] **Step 5: Commit and open the Phase 1 PR**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add the Diariz n8n HTTP transport"
```
```bash
git push -u origin feat/n8n-node-scaffold
```

Open the PR. Body: **no platform release** (no `version.json` bump - the package versions
independently and has not been published yet), neither a server redeploy nor a desktop
release, and it is the scaffold half of the n8n node arc.

---

# Phase 2: The Trigger node

**Branch:** `feat/n8n-trigger-node`

### Task 2.1: Signature verification (pure)

**Files:**
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/signature.ts`
- Test: `integrations/n8n-nodes-diariz/test/signature.test.ts`

**Interfaces:**
- Produces: `verifyWebhookSignature(input: { secret: string; headers: Record<string, string | string[] | undefined>; rawBody: string; now?: Date; toleranceSeconds?: number }): { ok: true } | { ok: false; reason: string }`. Task 2.3 calls this and nothing else for verification.

- [ ] **Step 1: Write the failing test**

The expected signature below is `v1,base64(HMAC-SHA256("whsec_test", "evt_1.1750000000.{\"id\":\"evt_1\"}"))`.
Compute the literal once and paste it in, so the test asserts a fixed vector rather than
recomputing the implementation's own arithmetic:

```bash
node -e 'const c=require("crypto");console.log("v1,"+c.createHmac("sha256","whsec_test").update("evt_1.1750000000."+JSON.stringify({id:"evt_1"})).digest("base64"))'
```

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { verifyWebhookSignature } from "../nodes/Diariz/signature";

const SECRET = "whsec_test";
const RAW = JSON.stringify({ id: "evt_1" });
const TS = 1750000000;
const AT = new Date(TS * 1000);
const GOOD = "v1,PASTE_THE_COMPUTED_VALUE_HERE";

const headers = (over: Record<string, string> = {}) => ({
  "webhook-id": "evt_1",
  "webhook-timestamp": String(TS),
  "webhook-signature": GOOD,
  ...over,
});

test("accepts a correctly signed delivery", () => {
  assert.deepEqual(
    verifyWebhookSignature({ secret: SECRET, headers: headers(), rawBody: RAW, now: AT }),
    { ok: true },
  );
});

test("rejects a tampered body", () => {
  const r = verifyWebhookSignature({ secret: SECRET, headers: headers(), rawBody: RAW + " ", now: AT });
  assert.equal(r.ok, false);
});

test("rejects the wrong secret", () => {
  const r = verifyWebhookSignature({ secret: "whsec_other", headers: headers(), rawBody: RAW, now: AT });
  assert.equal(r.ok, false);
});

test("accepts when one of several space-delimited signatures matches", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: headers({ "webhook-signature": `v1,AAAA ${GOOD}` }),
    rawBody: RAW,
    now: AT,
  });
  assert.deepEqual(r, { ok: true });
});

test("rejects a replayed delivery outside the tolerance window", () => {
  const r = verifyWebhookSignature({
    secret: SECRET, headers: headers(), rawBody: RAW,
    now: new Date((TS + 601) * 1000),
  });
  assert.equal(r.ok, false);
  assert.match((r as { reason: string }).reason, /too old|timestamp/i);
});

test("rejects a missing header rather than throwing", () => {
  const h = headers();
  delete (h as any)["webhook-signature"];
  const r = verifyWebhookSignature({ secret: SECRET, headers: h, rawBody: RAW, now: AT });
  assert.equal(r.ok, false);
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - module not found.

- [ ] **Step 3: Write the implementation**

```ts
import { createHmac, timingSafeEqual } from "node:crypto";

export interface VerifyInput {
  secret: string;
  headers: Record<string, string | string[] | undefined>;
  /** The exact bytes Diariz sent. Never a re-serialised object - the signature covers the original JSON. */
  rawBody: string;
  now?: Date;
  toleranceSeconds?: number;
}

export type VerifyResult = { ok: true } | { ok: false; reason: string };

const DEFAULT_TOLERANCE_SECONDS = 300;

function header(headers: VerifyInput["headers"], name: string): string | undefined {
  const value = headers[name] ?? headers[name.toLowerCase()];
  return Array.isArray(value) ? value[0] : value;
}

function equals(a: string, b: string): boolean {
  const left = Buffer.from(a, "utf8");
  const right = Buffer.from(b, "utf8");
  return left.length === right.length && timingSafeEqual(left, right);
}

/** Standard Webhooks: v1,base64(HMAC-SHA256(secret, "<id>.<timestamp>.<body>")).
 *  Mirrors src/Diariz.Api/Webhooks/WebhookSigner.cs - the fixtures in
 *  tests/Diariz.Api.Tests keep the two implementations honest. */
export function verifyWebhookSignature(input: VerifyInput): VerifyResult {
  const id = header(input.headers, "webhook-id");
  const timestamp = header(input.headers, "webhook-timestamp");
  const signature = header(input.headers, "webhook-signature");
  if (!id || !timestamp || !signature) return { ok: false, reason: "Missing webhook signature headers." };

  const sent = Number(timestamp);
  if (!Number.isFinite(sent)) return { ok: false, reason: "The webhook timestamp was not a number." };

  const tolerance = input.toleranceSeconds ?? DEFAULT_TOLERANCE_SECONDS;
  const nowSeconds = Math.floor((input.now ?? new Date()).getTime() / 1000);
  if (Math.abs(nowSeconds - sent) > tolerance) {
    return { ok: false, reason: "The webhook timestamp is too old or too far in the future." };
  }

  const expected =
    "v1," + createHmac("sha256", input.secret).update(`${id}.${sent}.${input.rawBody}`).digest("base64");

  const matched = signature.split(" ").some((candidate) => equals(candidate.trim(), expected));
  return matched ? { ok: true } : { ok: false, reason: "The webhook signature did not match." };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: PASS, 6 signature tests.

- [ ] **Step 5: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: verify Standard Webhooks signatures in the n8n node"
```

---

### Task 2.2: Cross-language signing fixture

Locks the .NET signer and the TypeScript verifier to one shared vector, so a change to either
side fails a test rather than silently breaking deliveries in production.

**Files:**
- Create: `tests/Diariz.Api.Tests/WebhookSignerFixtureTests.cs`
- Create: `integrations/n8n-nodes-diariz/test/fixtures/signing-vectors.json` (written by the .NET test, committed)
- Modify: `integrations/n8n-nodes-diariz/test/signature.test.ts`

**Interfaces:**
- Consumes: `WebhookSigner.Sign` (.NET), `verifyWebhookSignature` (Task 2.1).
- Produces: `signing-vectors.json` shaped `{ vectors: [{ secret, webhookId, timestamp, body, signature }] }`.

- [ ] **Step 1: Write the failing .NET test**

```csharp
using System.Text.Json;
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

/// <summary>Writes the signing vectors the TypeScript n8n node verifies against, and asserts the committed
/// file still matches what this signer produces. Either side changing breaks one of the two suites.</summary>
public class WebhookSignerFixtureTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "integrations", "n8n-nodes-diariz", "test", "fixtures", "signing-vectors.json");

    private static readonly (string Secret, string Id, long Ts, string Body)[] Cases =
    {
        ("whsec_test", "evt_1", 1750000000L, """{"id":"evt_1"}"""),
        ("dz_whsec_abc123", "evt_2", 1750000123L,
            """{"id":"evt_2","type":"recording.transcribed","created":"2026-07-24T10:00:00.0000000Z","data":{"recordingId":"6f1b...","name":"Weekly sync"}}"""),
        ("dz_whsec_unicode", "evt_3", 1750000456L, """{"id":"evt_3","data":{"name":"Réunion hebdomadaire ✅"}}"""),
    };

    [Fact]
    public void CommittedVectors_MatchTheSigner()
    {
        var expected = Cases.Select(c => new
        {
            secret = c.Secret, webhookId = c.Id, timestamp = c.Ts, body = c.Body,
            signature = WebhookSigner.Sign(c.Secret, c.Id, c.Ts, c.Body),
        }).ToArray();

        var json = JsonSerializer.Serialize(new { vectors = expected },
            new JsonSerializerOptions { WriteIndented = true });

        var full = Path.GetFullPath(FixturePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full) || File.ReadAllText(full).ReplaceLineEndings("\n") != json.ReplaceLineEndings("\n"))
        {
            File.WriteAllText(full, json);
            Assert.Fail($"Signing vectors regenerated at {full}. Commit the file and re-run.");
        }
    }
}
```

The unicode case matters: it proves both sides agree on UTF-8 byte handling, which is exactly
where a cross-language HMAC usually diverges.

- [ ] **Step 2: Run it to write the fixture and see it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookSignerFixtureTests"
```

Expected: FAIL with "Signing vectors regenerated" and a new `signing-vectors.json` on disk.

- [ ] **Step 3: Re-run to confirm it now passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WebhookSignerFixtureTests"
```

Expected: PASS.

- [ ] **Step 4: Add the TypeScript side**

Append to `test/signature.test.ts`:

```ts
import vectors from "./fixtures/signing-vectors.json" with { type: "json" };

test("accepts every vector produced by the .NET signer", () => {
  for (const v of vectors.vectors) {
    const result = verifyWebhookSignature({
      secret: v.secret,
      headers: {
        "webhook-id": v.webhookId,
        "webhook-timestamp": String(v.timestamp),
        "webhook-signature": v.signature,
      },
      rawBody: v.body,
      now: new Date(v.timestamp * 1000),
    });
    assert.deepEqual(result, { ok: true }, `vector ${v.webhookId} failed`);
  }
});
```

- [ ] **Step 5: Run both suites**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: PASS, including the vector test.

- [ ] **Step 6: Commit**

```bash
git add tests/Diariz.Api.Tests/WebhookSignerFixtureTests.cs integrations/n8n-nodes-diariz
git commit -m "test: lock the webhook signature contract across .NET and TypeScript"
```

---

### Task 2.3: The Trigger node

**Files:**
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/DiarizTrigger.node.ts`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/events.ts`
- Test: `integrations/n8n-nodes-diariz/test/trigger.test.ts`

**Interfaces:**
- Consumes: `diarizApiRequest` (Task 1.3), `verifyWebhookSignature` (Task 2.1).
- Produces: `EVENT_OPTIONS` in `events.ts` - the nine `{ name, value, description }` entries, reused by Phase 4's Webhook resource.

- [ ] **Step 1: Write the failing test**

Pure-logic tests only; the n8n lifecycle is verified live in Task 5.2.

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { EVENT_OPTIONS } from "../nodes/Diariz/events";
import { DiarizTrigger } from "../nodes/Diariz/DiarizTrigger.node";

test("offers all nine subscribable events", () => {
  assert.equal(EVENT_OPTIONS.length, 9);
  const values = EVENT_OPTIONS.map((o) => o.value);
  for (const expected of [
    "recording.created", "recording.transcribed", "recording.transcription_failed",
    "recording.summarized", "recording.minutes_ready", "recording.action_items_ready",
    "recording.tags_ready", "formula_result.completed", "formula_result.failed",
  ]) assert.ok(values.includes(expected), `missing ${expected}`);
});

test("never offers the internal ping event", () => {
  assert.ok(!EVENT_OPTIONS.some((o) => o.value === "webhook.ping"));
});

test("uses plain hyphens in every user-facing string", () => {
  const text = JSON.stringify(new DiarizTrigger().description) + JSON.stringify(EVENT_OPTIONS);
  assert.ok(!/[–—]/.test(text), "found an en or em dash in user-facing copy");
});

test("requests the raw body so signatures can be verified", () => {
  const webhook = new DiarizTrigger().description.webhooks![0];
  assert.equal(webhook.rawBody, true);
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - modules not found.

- [ ] **Step 3: Write `events.ts`**

```ts
import type { INodePropertyOptions } from "n8n-workflow";

/** Mirrors WebhookEventTypes.Subscribable in src/Diariz.Api/Webhooks/WebhookEventTypes.cs.
 *  Keys are append-only on the server, so this list only ever grows. */
export const EVENT_OPTIONS: INodePropertyOptions[] = [
  { name: "Recording Created", value: "recording.created", description: "A recording was uploaded or captured, before any transcription" },
  { name: "Recording Transcribed", value: "recording.transcribed", description: "The transcript is ready, with speaker labels and timings" },
  { name: "Transcription Failed", value: "recording.transcription_failed", description: "Transcription could not be completed" },
  { name: "Summary Ready", value: "recording.summarized", description: "The AI summary is ready, and rides along in the event" },
  { name: "Meeting Minutes Ready", value: "recording.minutes_ready", description: "The meeting minutes document is ready" },
  { name: "Action Items Ready", value: "recording.action_items_ready", description: "Action items were extracted from the transcript" },
  { name: "Tags Ready", value: "recording.tags_ready", description: "Topic tags were generated for the recording" },
  { name: "Formula Result Completed", value: "formula_result.completed", description: "A formula finished and produced a document" },
  { name: "Formula Result Failed", value: "formula_result.failed", description: "A formula run failed" },
];
```

- [ ] **Step 4: Write the trigger node**

```ts
import type {
  IDataObject,
  IHookFunctions,
  INodeType,
  INodeTypeDescription,
  IWebhookFunctions,
  IWebhookResponseData,
} from "n8n-workflow";
import { NodeOperationError } from "n8n-workflow";
import { diarizApiRequest } from "./transport/request";
import { verifyWebhookSignature } from "./signature";
import { EVENT_OPTIONS } from "./events";

export class DiarizTrigger implements INodeType {
  description: INodeTypeDescription = {
    displayName: "Diariz Trigger",
    name: "diarizTrigger",
    icon: "file:diariz.svg",
    group: ["trigger"],
    version: 1,
    subtitle: '={{$parameter["events"].join(", ")}}',
    description: "Starts a workflow when something happens in Diariz",
    defaults: { name: "Diariz Trigger" },
    inputs: [],
    outputs: ["main"],
    credentials: [{ name: "diarizApi", required: true }],
    webhooks: [
      { name: "default", httpMethod: "POST", responseMode: "onReceived", path: "webhook", rawBody: true },
    ],
    properties: [
      {
        displayName: "Events",
        name: "events",
        type: "multiOptions",
        required: true,
        default: [],
        options: EVENT_OPTIONS,
        description: "Which Diariz events start this workflow",
      },
      {
        displayName: "Simplify",
        name: "simplify",
        type: "boolean",
        default: true,
        description: "Whether to return only the event data instead of the full event envelope",
      },
    ],
  };

  webhookMethods = {
    default: {
      async checkExists(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        // Without the secret we cannot verify deliveries, and Diariz only ever returns it once,
        // so treat a secret-less subscription as absent and let create() replace it.
        if (!data.subscriptionId || !data.secret) return false;

        const url = this.getNodeWebhookUrl("default");
        const existing = (await diarizApiRequest.call(this, "GET", "/api/user/webhooks")) as IDataObject[];
        return existing.some((s) => s.id === data.subscriptionId && s.url === url);
      },

      async create(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        const url = this.getNodeWebhookUrl("default") as string;
        const events = this.getNodeParameter("events") as string[];
        if (events.length === 0) {
          throw new NodeOperationError(this.getNode(), "Choose at least one event before activating.");
        }

        // A leftover subscription on this URL would double-deliver; clear it first.
        const existing = (await diarizApiRequest.call(this, "GET", "/api/user/webhooks")) as IDataObject[];
        for (const stale of existing.filter((s) => s.url === url)) {
          await diarizApiRequest.call(this, "DELETE", `/api/user/webhooks/${stale.id}`);
        }

        const created = (await diarizApiRequest.call(this, "POST", "/api/user/webhooks", {
          name: `n8n: ${this.getWorkflow().name ?? "workflow"}`,
          url,
          eventTypes: events,
        })) as IDataObject;

        data.subscriptionId = created.id;
        data.secret = created.secret; // returned exactly once
        return true;
      },

      async delete(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        if (data.subscriptionId) {
          await diarizApiRequest.call(this, "DELETE", `/api/user/webhooks/${data.subscriptionId}`);
        }
        delete data.subscriptionId;
        delete data.secret;
        return true;
      },
    },
  };

  async webhook(this: IWebhookFunctions): Promise<IWebhookResponseData> {
    const data = this.getWorkflowStaticData("node");
    const req = this.getRequestObject();
    const raw = (req as unknown as { rawBody?: Buffer }).rawBody?.toString("utf8") ?? "";

    const verified = verifyWebhookSignature({
      secret: (data.secret as string) ?? "",
      headers: this.getHeaderData() as Record<string, string | string[] | undefined>,
      rawBody: raw,
    });
    if (!verified.ok) {
      // 401 with no items: Diariz retries on its backoff schedule, which is right for a genuine
      // misconfiguration and harmless for a forged request.
      return { noWebhookResponse: false, webhookResponse: { status: 401, body: verified.reason } };
    }

    const envelope = JSON.parse(raw) as IDataObject;
    const simplify = this.getNodeParameter("simplify", true) as boolean;
    const json = simplify ? (envelope.data as IDataObject) : envelope;

    return { workflowData: [this.helpers.returnJsonArray([json])] };
  }
}
```

- [ ] **Step 5: Run tests and lint**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint && npm run build
```

Expected: PASS, lint clean, build succeeds. Resolve any n8n-linter naming complaints - they
encode conventions the verification review will also apply.

- [ ] **Step 6: Commit and open the Phase 2 PR**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add the Diariz Trigger node"
```
```bash
git push -u origin feat/n8n-trigger-node
```

State in the PR body: no platform release, no redeploy, no desktop release.

---

# Phase 3: Codegen and the generated tail

**Branch:** `feat/n8n-node-codegen`

### Task 3.1: Export the OpenAPI snapshot from the test host

**Files:**
- Create: `tests/Diariz.Api.Tests/OpenApiSnapshotTests.cs`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` (generated, committed)

**Interfaces:**
- Consumes: the same in-process host pattern as `OpenApiDocumentTests.GenerateDocumentAsync()`.
- Produces: the committed snapshot, which Task 3.2's generator reads and Task 3.4's CI job diffs.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Tests;

/// <summary>Keeps the snapshot the n8n node generates from in step with the real document. Regenerating is
/// the fix, not editing the snapshot - see integrations/n8n-nodes-diariz/scripts/generate.ts.</summary>
public class OpenApiSnapshotTests
{
    private static readonly string SnapshotPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "integrations", "n8n-nodes-diariz", "nodes", "Diariz", "generated", "openapi.snapshot.json");

    [Fact]
    public async Task Snapshot_MatchesTheCurrentDocument()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(RecordingsController).Assembly)
            .AddJsonOptions(o => JsonConfig.Apply(o.JsonSerializerOptions));
        builder.Services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = desc => OpenApiCuration.ShouldInclude(desc.RelativePath);
            options.AddDocumentTransformer<OpenApiCuration.SecuritySchemeTransformer>();
            options.AddDocumentTransformer<OpenApiCuration.TagDescriptionsTransformer>();
        });

        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi("/api/openapi/{documentName}.json");
        await app.StartAsync();

        var body = await app.GetTestClient().GetStringAsync("/api/openapi/v1.json");
        // Re-serialise indented so the committed file diffs readably and stably.
        using var doc = JsonDocument.Parse(body);
        var normalized = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });

        var full = Path.GetFullPath(SnapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full) || File.ReadAllText(full).ReplaceLineEndings("\n") != normalized.ReplaceLineEndings("\n"))
        {
            File.WriteAllText(full, normalized);
            Assert.Fail($"OpenAPI snapshot regenerated at {full}. Run 'npm run generate' in the node package, then commit both.");
        }
    }
}
```

- [ ] **Step 2: Run it to write the snapshot and see it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"
```

Expected: FAIL with "OpenAPI snapshot regenerated".

- [ ] **Step 3: Re-run to confirm it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Diariz.Api.Tests/OpenApiSnapshotTests.cs integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json
git commit -m "test: snapshot the published OpenAPI document for the n8n node"
```

---

### Task 3.2: The generator

**Files:**
- Create: `integrations/n8n-nodes-diariz/scripts/generate.ts`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/index.ts` (generated, committed)
- Test: `integrations/n8n-nodes-diariz/test/generate.test.ts`

**Interfaces:**
- Consumes: `openapi.snapshot.json` (Task 3.1).
- Produces:
  - `toResourceName(tag: string): string` - `"SectionAttachments"` -> `"Section Attachment"`.
  - `firstParagraph(markdown: string): string` - strips Markdown emphasis and takes the first paragraph.
  - `buildOperations(doc: OpenApiDoc): GeneratedResource[]` where `GeneratedResource = { tag: string; displayName: string; value: string; operations: GeneratedOperation[] }` and `GeneratedOperation = { value: string; displayName: string; description: string; method: string; path: string; pathParams: string[]; queryParams: {name: string; required: boolean; description: string}[]; hasBody: boolean }`.
  - `EXCLUDED_TAGS = ["Auth"]`.
- Task 3.3 consumes `generated/index.ts`'s default export.

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import doc from "../nodes/Diariz/generated/openapi.snapshot.json" with { type: "json" };
import { buildOperations, firstParagraph, toResourceName, EXCLUDED_TAGS } from "../scripts/generate";

const resources = buildOperations(doc as any);

test("splits a PascalCase tag into a readable singular resource name", () => {
  assert.equal(toResourceName("SectionAttachments"), "Section Attachment");
  assert.equal(toResourceName("Recordings"), "Recording");
  assert.equal(toResourceName("Storage"), "Storage");
});

test("takes the first paragraph and strips Markdown", () => {
  assert.equal(
    firstParagraph("The **50 most recent** deliveries.\n\nFailed ones are retried."),
    "The 50 most recent deliveries.",
  );
});

test("excludes the Auth resource", () => {
  assert.deepEqual(EXCLUDED_TAGS, ["Auth"]);
  assert.ok(!resources.some((r) => r.tag === "Auth"));
});

test("covers every published tag except the exclusions", () => {
  const tags = new Set<string>();
  for (const path of Object.values(doc.paths as Record<string, any>)) {
    for (const op of Object.values(path as Record<string, any>)) {
      if (op && typeof op === "object" && Array.isArray((op as any).tags)) {
        for (const t of (op as any).tags) tags.add(t);
      }
    }
  }
  for (const tag of tags) {
    if (EXCLUDED_TAGS.includes(tag)) continue;
    assert.ok(resources.some((r) => r.tag === tag), `no generated resource for ${tag}`);
  }
});

test("gives every operation a display name and a description", () => {
  for (const r of resources) {
    for (const op of r.operations) {
      assert.ok(op.displayName.length > 0, `${r.tag}.${op.value} has no display name`);
      assert.ok(op.description.length > 0, `${r.tag}.${op.value} has no description`);
    }
  }
});

test("uses plain hyphens in all generated copy", () => {
  assert.ok(!/[–—]/.test(JSON.stringify(resources)));
});

test("records path parameters for every templated segment", () => {
  const withId = resources.flatMap((r) => r.operations).find((o) => o.path.includes("{id}"));
  assert.ok(withId);
  assert.ok(withId!.pathParams.includes("id"));
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - `../scripts/generate` not found.

- [ ] **Step 3: Write the generator**

`scripts/generate.ts` exports the pure functions above plus a `main()` that writes
`nodes/Diariz/generated/index.ts`. Requirements the tests pin down:

- `toResourceName`: split PascalCase on capital boundaries, join with spaces, singularise a
  trailing `s` (but not `ss`, and leave `Storage` alone - it has no trailing `s` to strip).
- `firstParagraph`: take text up to the first `\n\n`, then remove `**`, `` ` ``, and `[text](url)`
  wrappers, collapse whitespace, and trim. Replace any `–` or `—` with `-` defensively.
- `buildOperations`: walk `paths` -> methods, skip anything without `tags`, skip `EXCLUDED_TAGS`,
  derive `value` as a camelCase slug of the summary (deduplicated within a resource by appending
  `2`, `3`, ... if two summaries collide), collect `{...}` path params in order, map `parameters`
  with `in: "query"`, and set `hasBody` when a `requestBody` exists.
- `main()` writes a file exporting `const GENERATED: GeneratedResource[] = [...]` as a JSON
  literal plus `export default GENERATED;`, preceded by a `// Generated by scripts/generate.ts - do not edit.` banner.

Add the npm script (already present from Task 1.1): `"generate": "node --experimental-strip-types scripts/generate.ts"`.

- [ ] **Step 4: Generate and run the tests**

```bash
cd integrations/n8n-nodes-diariz && npm run generate && npm test
```

Expected: `generated/index.ts` written; all generate tests PASS.

- [ ] **Step 5: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: generate n8n operations from the Diariz OpenAPI document"
```

---

### Task 3.3: Wire the generated tail into the node

**Files:**
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/Diariz.node.ts`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/Diariz.node.json`
- Create: `integrations/n8n-nodes-diariz/nodes/Diariz/generatedProperties.ts`
- Test: `integrations/n8n-nodes-diariz/test/node.test.ts`

**Interfaces:**
- Consumes: `GENERATED` (Task 3.2), `diarizApiRequest` (Task 1.3).
- Produces: `Diariz` node class with a `resource`/`operation` property pair and an `execute` that resolves a generated operation to an HTTP call. Phase 4 prepends hand-authored resources to the same `resource` options list and short-circuits them ahead of the generated router.

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { Diariz } from "../nodes/Diariz/Diariz.node";
import { buildPath } from "../nodes/Diariz/generatedProperties";

const node = new Diariz();

test("declares the credential and a single main output", () => {
  assert.deepEqual(node.description.credentials, [{ name: "diarizApi", required: true }]);
  assert.deepEqual(node.description.outputs, ["main"]);
});

test("exposes a resource selector covering the generated tail", () => {
  const resource = node.description.properties.find((p) => p.name === "resource")!;
  const values = (resource.options as { value: string }[]).map((o) => o.value);
  assert.ok(values.includes("room"));
  assert.ok(values.includes("userProfile"));
  assert.ok(!values.includes("auth"));
});

test("offers Custom API Call on every resource", () => {
  const operations = node.description.properties.filter((p) => p.name === "operation");
  assert.ok(operations.length > 0);
  for (const op of operations) {
    const values = (op.options as { value: string }[]).map((o) => o.value);
    assert.ok(values.includes("customApiCall"), `missing custom call on ${JSON.stringify(op.displayOptions)}`);
  }
});

test("substitutes path parameters", () => {
  assert.equal(
    buildPath("/api/recordings/{id}/formula-results/{resultId}", { id: "a", resultId: "b" }),
    "/api/recordings/a/formula-results/b",
  );
});

test("rejects a missing path parameter rather than sending a literal brace", () => {
  assert.throws(() => buildPath("/api/recordings/{id}", {}), /id/);
});

test("uses plain hyphens throughout the node description", () => {
  assert.ok(!/[–—]/.test(JSON.stringify(node.description)));
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - modules not found.

- [ ] **Step 3: Write the implementation**

`generatedProperties.ts` turns `GENERATED` into n8n properties:

```ts
import type { INodeProperties } from "n8n-workflow";
import GENERATED from "./generated";

export function buildPath(template: string, params: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, name: string) => {
    const value = params[name];
    if (value === undefined || value === "") {
      throw new Error(`This operation needs a value for "${name}".`);
    }
    return encodeURIComponent(value);
  });
}

export const generatedResourceOptions = GENERATED.map((r) => ({
  name: r.displayName,
  value: r.value,
  description: `Operations on ${r.displayName.toLowerCase()}`,
}));

export const generatedProperties: INodeProperties[] = GENERATED.flatMap((r) => [
  {
    displayName: "Operation",
    name: "operation",
    type: "options",
    noDataExpression: true,
    displayOptions: { show: { resource: [r.value] } },
    default: r.operations[0]?.value ?? "customApiCall",
    options: [
      ...r.operations.map((o) => ({ name: o.displayName, value: o.value, description: o.description, action: o.displayName })),
      { name: "Custom API Call", value: "customApiCall", description: "Call any Diariz endpoint directly", action: "Custom API call" },
    ],
  },
  // ... one string property per path param, one collection for query params, one json body field
]);
```

`Diariz.node.ts` composes `resource` (generated options now, hand-authored prepended in Phase
4), spreads `generatedProperties`, and implements `execute()`: read `resource` and `operation`,
find the matching `GeneratedOperation`, `buildPath` it, collect query params, and call
`diarizApiRequest`. `customApiCall` reads explicit method/path/query/body fields instead.
Per-item `try/catch` honouring `this.continueOnFail()`, which n8n expects of every node.

`Diariz.node.json` (codex metadata):

```json
{
  "node": "n8n-nodes-diariz.diariz",
  "nodeVersion": "1.0",
  "codexVersion": "1.0",
  "categories": ["Productivity", "Utility"],
  "resources": {
    "credentialDocumentation": [{ "url": "https://github.com/kenhayward/Diariz/tree/main/integrations/n8n-nodes-diariz" }],
    "primaryDocumentation": [{ "url": "https://github.com/kenhayward/Diariz/tree/main/integrations/n8n-nodes-diariz" }]
  }
}
```

- [ ] **Step 4: Run tests, lint and build**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint && npm run build
```

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add the Diariz action node with its generated operations"
```

---

### Task 3.4: CI drift guard

**Files:**
- Create or modify: `.github/workflows/` - inspect existing workflows first with `ls .github/workflows` and follow their runner and setup conventions (this repo uses a self-hosted Windows runner; check whether `node` and `dotnet` are already provisioned there rather than assuming `actions/setup-*` works).

**Interfaces:**
- Consumes: Task 3.1's snapshot test, Task 3.2's `npm run generate`.
- Produces: a build that fails when the API's public surface changes without the node being regenerated.

- [ ] **Step 1: Inspect the existing CI setup**

```bash
ls .github/workflows && cat .github/workflows/*.yml | head -60
```

- [ ] **Step 2: Add the job**

Add a job (or steps to the existing build job) that runs:

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"
```
```bash
cd integrations/n8n-nodes-diariz && npm ci && npm run generate && npm test && npm run lint && npm run build
```
```bash
git diff --exit-code -- integrations/n8n-nodes-diariz
```

The snapshot test rewrites the file and fails when the document has moved; the final
`git diff --exit-code` catches a snapshot that changed but was not regenerated into
`generated/index.ts`.

- [ ] **Step 3: Verify locally by simulating drift**

Add a throwaway endpoint to any published controller with `[EndpointSummary]` and
`[EndpointDescription]`, then run the three commands above.

Expected: the snapshot test FAILS with "OpenAPI snapshot regenerated". Revert the throwaway
endpoint and confirm everything passes again.

- [ ] **Step 4: Commit and open the Phase 3 PR**

```bash
git add .github/workflows integrations/n8n-nodes-diariz
git commit -m "ci: fail the build when the n8n node drifts from the API"
```
```bash
git push -u origin feat/n8n-node-codegen
```

---

# Phase 4: Curated core resources

**Branch:** `feat/n8n-core-resources`

Each task follows the same shape: a `descriptions/<Resource>Description.ts` exporting
`<resource>Operations: INodeProperties[]` and `<resource>Fields: INodeProperties[]`, an
`execute` branch in `Diariz.node.ts`, tests, lint, build, commit.

**Documented decision on code volume:** Task 4.1 carries complete code as the reference
implementation. Tasks 4.2 to 4.6 specify each operation by exact HTTP method, path, parameters
and n8n field types in the tables below, and follow 4.1's structure. The tables are the
requirements - they are complete, not placeholders - but the descriptor TypeScript is
mechanical from them and is not reproduced verbatim five more times.

### Task 4.1: Recording resource (reference implementation)

**Files:**
- Create: `nodes/Diariz/descriptions/RecordingDescription.ts`
- Modify: `nodes/Diariz/Diariz.node.ts`
- Create: `nodes/Diariz/methods/loadOptions.ts`
- Test: `test/recording.test.ts`

**Interfaces:**
- Consumes: `diarizApiRequest`, `applyLimit`, `buildPath`.
- Produces: `loadOptions` methods `getRecordings`, `getFolders`, `getRooms`, `getFormulas`, `getMeetingTypes` - reused by every later core resource. Signature: `async getRecordings(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]>`.

Operations:

| Operation | Method | Path | Notes |
|---|---|---|---|
| Get | GET | `/api/recordings/{id}` | |
| Get Many | GET | `/api/recordings` | optional `roomId`; Return All / Limit via `applyLimit` |
| Upload | POST | `/api/recordings` | multipart: `audio` from a binary property, `title`, `durationMs=0`, `source=Upload`, optional `sectionId`, `roomId` |
| Delete | DELETE | `/api/recordings/{id}` | |
| Rename | PUT | `/api/recordings/{id}/name` | |
| Move to Folder | PUT | `/api/recordings/{id}/section` | |
| Re-Transcribe | POST | `/api/recordings/{id}/retranscribe` | |
| Summarize | POST | `/api/recordings/{id}/summarize` | |
| Generate Minutes | POST | `/api/recordings/{id}/meeting-minutes/generate` | |
| Download Transcript | GET | `/api/recordings/{id}/transcript.{format}` | format: txt, md, rtf, srt -> binary output |
| Download Audio | GET | `/api/recordings/{id}/audio` | binary output |
| Get Audio URL | GET | `/api/recordings/{id}/audio-url` | |
| Email | POST | `/api/recordings/{id}/email` | |
| Share | POST | `/api/recordings/{id}/share` | |

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { recordingOperations, recordingFields } from "../nodes/Diariz/descriptions/RecordingDescription";

test("offers every recording operation", () => {
  const values = (recordingOperations[0].options as { value: string }[]).map((o) => o.value);
  for (const expected of [
    "get", "getAll", "upload", "delete", "rename", "move", "retranscribe",
    "summarize", "generateMinutes", "downloadTranscript", "downloadAudio",
    "getAudioUrl", "email", "share", "customApiCall",
  ]) assert.ok(values.includes(expected), `missing ${expected}`);
});

test("lets the user pick a recording from a dropdown", () => {
  const id = recordingFields.find(
    (f) => f.name === "recordingId" && (f.displayOptions?.show?.operation as string[])?.includes("get"),
  )!;
  assert.equal(id.typeOptions?.loadOptionsMethod, "getRecordings");
});

test("offers Return All with a limit on Get Many", () => {
  assert.ok(recordingFields.some((f) => f.name === "returnAll"));
  const limit = recordingFields.find((f) => f.name === "limit")!;
  assert.equal(limit.displayOptions?.hide?.returnAll?.[0], true);
});

test("offers the four transcript formats", () => {
  const format = recordingFields.find((f) => f.name === "format")!;
  assert.deepEqual((format.options as { value: string }[]).map((o) => o.value), ["txt", "md", "rtf", "srt"]);
});

test("uses plain hyphens in all recording copy", () => {
  assert.ok(!/[–—]/.test(JSON.stringify([recordingOperations, recordingFields])));
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - module not found.

- [ ] **Step 3: Write `methods/loadOptions.ts`**

```ts
import type { ILoadOptionsFunctions, INodePropertyOptions, IDataObject } from "n8n-workflow";
import { diarizApiRequest } from "../transport/request";

async function options(
  ctx: ILoadOptionsFunctions, path: string, label: (row: IDataObject) => string,
): Promise<INodePropertyOptions[]> {
  const rows = (await diarizApiRequest.call(ctx, "GET", path)) as IDataObject[];
  return rows
    .map((row) => ({ name: label(row), value: row.id as string }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

export async function getRecordings(this: ILoadOptionsFunctions) {
  return options(this, "/api/recordings", (r) => (r.name as string) || (r.title as string) || "Untitled");
}
export async function getFolders(this: ILoadOptionsFunctions) {
  return options(this, "/api/sections", (r) => r.name as string);
}
export async function getRooms(this: ILoadOptionsFunctions) {
  return options(this, "/api/rooms", (r) => r.name as string);
}
export async function getFormulas(this: ILoadOptionsFunctions) {
  return options(this, "/api/formulas", (r) => r.name as string);
}
export async function getMeetingTypes(this: ILoadOptionsFunctions) {
  return options(this, "/api/meeting-types", (r) => r.name as string);
}
```

Confirm each list endpoint's actual path and its display field before writing (`/api/sections`,
`/api/rooms`, `/api/meeting-types` - verify against the controllers' `[Route]` attributes).

- [ ] **Step 4: Write `RecordingDescription.ts` and the execute branch**

Descriptor conventions to follow throughout:

- `recordingOperations`: one `INodeProperties` with `name: "operation"`, `type: "options"`, `noDataExpression: true`, `displayOptions: { show: { resource: ["recording"] } }`, and an `action` on every option (the n8n linter requires it).
- `recordingFields`: `recordingId` (`type: "options"`, `loadOptionsMethod: "getRecordings"`) shown for every operation that needs one; `returnAll` boolean plus `limit` (`default: 50`, `typeOptions: { minValue: 1 }`, hidden when `returnAll`); `format` options for the transcript download; `binaryPropertyName` for upload and downloads.
- Binary download: pass `{ encoding: "arraybuffer", json: false }` through `diarizApiRequest`'s `options` argument, then `this.helpers.prepareBinaryData`.
- Upload: build a `FormData` from `this.helpers.getBinaryDataBuffer(i, binaryPropertyName)` and send with `{ json: false }`.

In `Diariz.node.ts`, add `recording` to the front of the `resource` options, spread
`...recordingOperations, ...recordingFields` before the generated properties, register the
`loadOptions` methods on `methods = { loadOptions: { ... } }`, and add the `if (resource === "recording")` branch ahead of the generated router.

- [ ] **Step 5: Run tests, lint and build**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint && npm run build
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add the Recording resource to the n8n node"
```

---

### Task 4.2: Formula and Formula Result

Includes the **Wait for Completion** polling, the most behaviour-heavy operation in the node.

**Files:**
- Create: `nodes/Diariz/descriptions/FormulaDescription.ts`, `nodes/Diariz/descriptions/FormulaResultDescription.ts`
- Create: `nodes/Diariz/transport/poll.ts`
- Modify: `nodes/Diariz/Diariz.node.ts`
- Test: `test/formula.test.ts`

**Interfaces:**
- Consumes: `diarizApiRequest`, `getFormulas`, `getRecordings`.
- Produces: `pollUntilTerminal<T>(fetch: () => Promise<T>, isTerminal: (v: T) => "ready" | "failed" | "pending", opts: { intervalMs: number; timeoutMs: number; sleep?: (ms: number) => Promise<void>; now?: () => number }): Promise<T>` - pure enough to unit-test with an injected clock and sleep.

| Resource | Operation | Method | Path |
|---|---|---|---|
| Formula | Get Many | GET | `/api/formulas` |
| Formula | Get | GET | `/api/formulas/{id}` (verify it exists; if not, filter Get Many) |
| Formula | Create | POST | `/api/formulas` |
| Formula | Update | PUT | `/api/formulas/{id}` |
| Formula | Delete | DELETE | `/api/formulas/{id}` |
| Formula | Run on Recording | POST | `/api/recordings/{recordingId}/formulas/{formulaId}/run` |
| Formula | Run on Folder | POST | the folder run endpoint - confirm in `SectionFormulaResultsController` |
| Formula Result | Get Many | GET | `/api/recordings/{recordingId}/formula-results` |
| Formula Result | Get | GET | `/api/recordings/{recordingId}/formula-results/{id}` |
| Formula Result | Update | PUT | `/api/recordings/{recordingId}/formula-results/{id}` |
| Formula Result | Delete | DELETE | `/api/recordings/{recordingId}/formula-results/{id}` |
| Formula Result | Download | GET | `/api/recordings/{recordingId}/formula-results/{id}/download` (binary) |
| Formula Result | Email | POST | `/api/recordings/{recordingId}/formula-results/{id}/email` |

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { pollUntilTerminal } from "../nodes/Diariz/transport/poll";

const noSleep = async () => {};

test("returns as soon as the result is ready", async () => {
  const states = ["Generating", "Generating", "Ready"];
  let i = 0;
  const result = await pollUntilTerminal(
    async () => ({ status: states[i++] }),
    (v) => (v.status === "Ready" ? "ready" : v.status === "Failed" ? "failed" : "pending"),
    { intervalMs: 1, timeoutMs: 1000, sleep: noSleep },
  );
  assert.equal(result.status, "Ready");
  assert.equal(i, 3);
});

test("throws with the recorded error when the run fails", async () => {
  await assert.rejects(
    pollUntilTerminal(
      async () => ({ status: "Failed", error: "The LLM request timed out." }),
      (v) => (v.status === "Ready" ? "ready" : v.status === "Failed" ? "failed" : "pending"),
      { intervalMs: 1, timeoutMs: 1000, sleep: noSleep },
    ),
    /timed out/,
  );
});

test("gives up after the timeout and names the result", async () => {
  let clock = 0;
  await assert.rejects(
    pollUntilTerminal(
      async () => ({ status: "Generating", id: "res-1" }),
      () => "pending",
      { intervalMs: 1, timeoutMs: 10, sleep: noSleep, now: () => (clock += 4) },
    ),
    /still generating|timed out/i,
  );
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd integrations/n8n-nodes-diariz && npm test
```

Expected: FAIL - module not found.

- [ ] **Step 3: Implement `poll.ts` and the two descriptions**

`Run on Recording` fields: `recordingId` (dropdown), `formulaId` (dropdown), `waitForCompletion`
(boolean, default `true`), `pollIntervalSeconds` (default 3) and `timeoutSeconds` (default 300),
both shown only when waiting. On timeout the thrown message names the `formulaResultId` so the
workflow can fetch it later, as the spec requires.

- [ ] **Step 4: Run tests, lint and build**

```bash
cd integrations/n8n-nodes-diariz && npm test && npm run lint && npm run build
```

- [ ] **Step 5: Commit**

```bash
git add integrations/n8n-nodes-diariz
git commit -m "feat: add Formula and Formula Result resources with completion polling"
```

---

### Task 4.3: Folder, Search and Tag

**Files:** `descriptions/FolderDescription.ts`, `SearchDescription.ts`, `TagDescription.ts`; `Diariz.node.ts`; `test/folder.test.ts`

| Resource | Operation | Method | Path |
|---|---|---|---|
| Folder | Get Many | GET | `/api/sections` |
| Folder | Create | POST | `/api/sections` |
| Folder | Rename | PUT | `/api/sections/{id}` |
| Folder | Delete | DELETE | `/api/sections/{id}` |
| Folder | Get Page | GET | the section page endpoint - confirm in `SectionPageController` |
| Search | Search Transcripts | GET | `/api/search` with `q`, `roomId`, `sectionId`, `everywhere`, `from`, `to`, `speaker`, `limit` |
| Tag | Get Many | GET | `/api/tags` |

- [ ] **Step 1: Write the failing test** - assert the operation lists, that Search exposes all
  eight query parameters, that `q` is required, and the plain-hyphen rule.
- [ ] **Step 2: Run it and watch it fail**
- [ ] **Step 3: Implement, following Task 4.1's structure**
- [ ] **Step 4: `npm test && npm run lint && npm run build`**
- [ ] **Step 5: Commit** - `git commit -m "feat: add Folder, Search and Tag resources"`

---

### Task 4.4: Action Item, Attachment and Speaker Profile

**Files:** `descriptions/ActionItemDescription.ts`, `AttachmentDescription.ts`, `SpeakerProfileDescription.ts`; `Diariz.node.ts`; `test/actionItem.test.ts`

| Resource | Operation | Method | Path |
|---|---|---|---|
| Action Item | Get Many (All) | GET | `/api/actions` |
| Action Item | Get Many (Recording) | GET | `/api/recordings/{recordingId}/actions` |
| Action Item | Extract | POST | the extract endpoint in `RecordingActionsController` |
| Action Item | Create / Update / Delete | POST / PUT / DELETE | `/api/recordings/{recordingId}/actions[/{id}]` |
| Action Item | Bulk Complete | POST | the bulk endpoint in `ActionsController` |
| Attachment | Get Many / Add File / Add URL / Rename / Delete / Download | GET / POST / POST / PUT / DELETE / GET | `/api/recordings/{recordingId}/attachments[...]` |
| Speaker Profile | Get Many / Create / Rename / Merge / Delete | GET / POST / PUT / POST / DELETE | `/api/speaker-profiles[...]` |

Confirm every path against the controllers before writing; the table names the controller where
a route needs looking up rather than guessing at it.

- [ ] **Step 1: Write the failing test** - operation coverage, binary property on Add File and
  Download, plain hyphens.
- [ ] **Step 2: Run it and watch it fail**
- [ ] **Step 3: Implement**
- [ ] **Step 4: `npm test && npm run lint && npm run build`**
- [ ] **Step 5: Commit** - `git commit -m "feat: add Action Item, Attachment and Speaker Profile resources"`

---

### Task 4.5: Chat over SSE

**Files:** `descriptions/ChatDescription.ts`, `transport/sse.ts`, `Diariz.node.ts`, `test/sse.test.ts`

**Interfaces:**
- Produces: `accumulateSse(stream: AsyncIterable<Buffer | string>): Promise<{ text: string; events: unknown[] }>`.

| Operation | Method | Path |
|---|---|---|
| Ask | POST | `/api/chat/stream` (`text/event-stream`) |
| List Conversations | GET | `/api/chat/conversations` |
| Get Conversation | GET | `/api/chat/conversations/{id}` |
| Delete Conversation | DELETE | `/api/chat/conversations/{id}` |

- [ ] **Step 1: Write the failing test**

```ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { accumulateSse } from "../nodes/Diariz/transport/sse";

async function* chunks(...parts: string[]) { for (const p of parts) yield Buffer.from(p, "utf8"); }

test("concatenates deltas across frames", async () => {
  const r = await accumulateSse(chunks(
    'data: {"delta":"Hello"}\n\n',
    'data: {"delta":" world"}\n\n',
  ));
  assert.equal(r.text, "Hello world");
});

test("handles a frame split across chunk boundaries", async () => {
  const r = await accumulateSse(chunks('data: {"del', 'ta":"split"}\n\n'));
  assert.equal(r.text, "split");
});

test("ignores the terminator and keeps non-delta events", async () => {
  const r = await accumulateSse(chunks('data: {"delta":"a"}\n\n', 'data: [DONE]\n\n'));
  assert.equal(r.text, "a");
});
```

Read `ChatController.cs:216` first and match the real frame shape - the test above assumes a
`delta` field, and it must assert what the server actually sends.

- [ ] **Step 2: Run it and watch it fail**
- [ ] **Step 3: Implement `accumulateSse` (buffer across chunks, split on `\n\n`, strip the `data: ` prefix, skip `[DONE]`) and the Chat description**
- [ ] **Step 4: `npm test && npm run lint && npm run build`**
- [ ] **Step 5: Commit** - `git commit -m "feat: add the Chat resource with SSE accumulation"`

---

### Task 4.6: Custom API Call on the core resources

**Files:** `Diariz.node.ts`, `test/customApiCall.test.ts`

- [ ] **Step 1: Write the failing test** - every core resource's operation list contains
  `customApiCall`, and the method, path, query and body fields appear when it is selected.
- [ ] **Step 2: Run it and watch it fail**
- [ ] **Step 3: Implement** - reuse Task 3.3's generated-side custom-call fields, shown for all resources.
- [ ] **Step 4: `npm test && npm run lint && npm run build`**
- [ ] **Step 5: Commit and open the Phase 4 PR**

```bash
git commit -m "feat: offer Custom API Call on every Diariz resource"
```
```bash
git push -u origin feat/n8n-core-resources
```

---

# Phase 5: Publish

**Branch:** `feat/n8n-node-publish`

### Task 5.1: Package documentation

**Files:** `integrations/n8n-nodes-diariz/README.md`, `CHANGELOG.md`

- [ ] **Step 1: Write the README** covering: what the node does; installation via Settings >
  Community Nodes; **the two administrator prerequisites** (API access, and Automations for the
  Trigger); creating a `dz_api_` token including the read-only and expiry choices; the nine
  trigger events; a worked example of the canonical workflow (Recording Transcribed -> Run
  Formula with Wait for Completion -> Slack); the note that a manual test execution registers a
  second temporary subscription; and the 20-subscription cap. Plain hyphens throughout.
- [ ] **Step 2: Write `CHANGELOG.md`** with a `0.1.0` entry.
- [ ] **Step 3: Commit** - `git commit -m "docs: document the n8n community node"`

---

### Task 5.2: Live verification

This is the first end-to-end proof; every prior task was unit-level.

- [ ] **Step 1: Pack the node**

```bash
cd integrations/n8n-nodes-diariz && npm run build && npm pack
```

- [ ] **Step 2: Confirm the dev server prerequisites**

On **dev.diariz.stocks-hayward.com**, sign in as a platform administrator and confirm API
access and Automations are both enabled. Create a `dz_api_` token with ReadWrite scope.

- [ ] **Step 3: Install on n8n.stocks-hayward.com**

Install the packed tarball through Settings > Community Nodes. Restart n8n if it does not pick
it up. Confirm both nodes and the credential appear in the node search.

- [ ] **Step 4: Verify the credential test**

Save a Diariz API credential and confirm it reports success. Then have the administrator turn
Automations off, re-test, and confirm the warning from Task 1.2 appears verbatim. Turn it back on.

- [ ] **Step 5: Build and activate the canonical workflow**

Diariz Trigger (Recording Transcribed, Summary Ready) -> Diariz (Formula: Run on Recording,
Wait for Completion) -> a Slack or webhook.site sink. Use the n8n MCP connector to build it.

Then confirm on the Diariz side: **Settings > Automations** shows a subscription named
`n8n: <workflow>`, and its delivery log is empty.

- [ ] **Step 6: Fire it**

Upload a short recording to the dev server. Expected:
- The trigger fires once transcription completes.
- The Diariz delivery log shows a `Delivered` row with HTTP 200.
- The formula node returns finished Markdown, not a `Generating` stub.
- A second event arrives when the summary is ready.

- [ ] **Step 7: Verify signature rejection**

Send a hand-crafted POST to the n8n webhook URL with a wrong `webhook-signature`.

Expected: 401, and **no** workflow execution.

- [ ] **Step 8: Verify deactivation cleans up**

Deactivate the workflow, then confirm the subscription has disappeared from Settings >
Automations.

- [ ] **Step 9: Record the results**

Note any deviation. Anything failing here goes back to the owning phase as a fix before Task 5.3.

---

### Task 5.3: Publish and submit

- [ ] **Step 1: Final gate**

```bash
cd integrations/n8n-nodes-diariz && npm run lint && npm test && npm run build
```

- [ ] **Step 2: Confirm the published contents**

```bash
cd integrations/n8n-nodes-diariz && npm pack --dry-run
```

Expected: `dist/**` only. No `test`, `scripts`, or `node_modules`.

- [ ] **Step 3: Publish**

```bash
cd integrations/n8n-nodes-diariz && npm publish --access public
```

- [ ] **Step 4: Verify a clean install**

Uninstall the local tarball on n8n.stocks-hayward.com and reinstall by package name
`n8n-nodes-diariz`. Confirm the workflow from Task 5.2 still runs.

- [ ] **Step 5: Submit for verification**

Check n8n's current verified-community-node requirements at submission time and confirm each:
name prefix, `n8n-community-node-package` keyword, zero runtime dependencies, clean linter, MIT
licence, no external calls. Then submit through their process.

- [ ] **Step 6: Commit** - `git commit -m "chore: publish n8n-nodes-diariz 0.1.0"`

---

### Task 5.4: Platform release and docs

The node is a new user-facing capability, so the platform ships a release even though no
platform code changed in this phase.

**Files:** `version.json` + three mirrors; `apps/web/src/lib/releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump Minor, reset Build** in `version.json`, `apps/web/package.json`,
  `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`. **Do not touch**
  `integrations/n8n-nodes-diariz/package.json` - it versions independently.
- [ ] **Step 2: Add the `RELEASES[0]` entry** with the real PR number, a headline, a prose
  summary, and `added` bullets for the node and its capabilities.
- [ ] **Step 3: Add the `CAPABILITIES` row** in `releases.ts` - one line, two columns, e.g.
  `| n8n | Trigger workflows on Diariz events and drive the full API from a published n8n community node. |`
- [ ] **Step 4: Update the README Features table and `docs/features.md`** in lockstep - a new
  row plus the matching prose bullet, both linking to the npm package.
- [ ] **Step 5: Update `docs/Overall_Synopsis_of_Platform.md`** - a new distributed artifact
  (the npm package), the four new webhook event types from Phase 0 if not already recorded there,
  and the CI drift guard. No `Data_Schema.md` change.
- [ ] **Step 6: Verify**

```bash
cd apps/web && npx vitest run src/lib/releases.test.ts && npm test
```
```bash
dotnet test tests/Diariz.Api.Tests
```

- [ ] **Step 7: Commit, push, open the PR**

```bash
git add -A && git commit -m "chore: release - n8n community node"
```
```bash
git push -u origin feat/n8n-node-publish
```

PR body: **no server redeploy and no desktop release required** (the web and API are unchanged
in this phase; the docs and version bump ship with the next ordinary deploy).

---

## Verification checklist

Run before declaring the arc complete:

- [ ] `dotnet test` (unit and integration) green, no warnings
- [ ] `cd apps/web && npm test` green
- [ ] `cd integrations/n8n-nodes-diariz && npm test && npm run lint && npm run build` green
- [ ] `dotnet build Diariz.slnx` clean
- [ ] The CI drift guard fails on a deliberately added endpoint and passes once regenerated
- [ ] The Task 5.2 live workflow fires end to end on n8n.stocks-hayward.com
- [ ] A forged signature is rejected with 401 and no execution
- [ ] Deactivating the workflow removes the Diariz subscription
- [ ] No em or en dashes in any node string, README line, release note or locale catalogue
- [ ] `integrations/n8n-nodes-diariz/package.json` has an empty `dependencies`
