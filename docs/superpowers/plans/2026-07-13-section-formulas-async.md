# Section/Folder Formulas (async, map-reduce) - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a user run a saved Formula over a **folder (section) and its sub-sections**, with the same
picker / results-list / open-edit-download-email UX as transcript Formulas, but with the run applied across
**every transcript in the section**. As part of this, **all formula runs become asynchronous** (background
job + `Generating -> Ready/Failed` status + poll), unifying the recording and section paths.

**Architecture:** A folder run is **map-reduce**: run the formula on each included transcript (map), then run
the **same formula prompt** over the concatenated per-meeting outputs (reduce) to produce the folder result.
Both recording and section runs go through one async pipeline (`formula-run-jobs` Redis stream -> a
`FormulaRunWorker` background service -> a static `FormulaRunProcessor` -> flips a pre-created result row to
`Ready`/`Failed`), mirroring the existing `section-summary-jobs` / `SectionSummaryWorker` /
`SectionSummaryProcessor` pattern exactly.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core/Postgres + StackExchange.Redis streams + SignalR; React 19 +
TS + Vite (RTL/vitest). No Python worker involved.

---

## Design decisions (locked with the user)

1. **Context aggregation = map-reduce.** For a folder: run the formula on **each** transcript's context
   (built from the formula's `FormulaContext` flags via the existing `FormulaContextBuilder`), then run the
   **same** `formula.Prompt` over the concatenation of those per-meeting outputs (each under a `## {meeting
   name}` heading, sharing a reduce char-budget with an "(+N more omitted)" note on overflow). The
   per-meeting "map" outputs are **ephemeral** - only the final folder result is persisted.
   - **0 included meetings** -> the run fails fast at enqueue (`400`, no row created).
   - **1 included meeting** -> the map output **is** the final result (skip the reduce pass - no pointless
     second LLM call).
2. **All formula runs are asynchronous.** The existing **synchronous** recording run is converted to the job
   pipeline. **Exception:** the MCP/chat `run_formula` tool stays synchronous - it targets a single
   recording (one fast call) and must return the result inside the tool call; it shares the run helpers but
   persists a `Ready` result inline. (Flag this to the user in review; it is the one deliberate carve-out.)
3. **UI only for now** - no folder variant of the `run_formula` chat/MCP tool (possible follow-up).
4. **Persistence:** a **new `SectionFormulaResult` entity** (additive, low risk - the existing
   `FormulaResult` recording table is untouched structurally except for two new columns). Both result tables
   gain `Status` + `Error`.
5. **Recording-set resolution = room-aware, one level deep.** Reuse the folder read path's model
   (`section + direct children`, resolved via `RoomRecordings` placement scoped to `section.RoomId`), **not**
   the personal-only `SectionSummaryProcessor.IncludedRecordingsAsync`. The section tree is two levels by
   design (sub-sections have no children), so "section + its sub-sections" = self + `ParentId == sectionId`.
6. **Access:** run requires **section membership** (`ViewableSectionAsync`); result **edit/delete** requires
   creator **or** room `ManageContents` (mirrors recording formula results: creator-or-owner). Recording-run
   access stays Phase-1 ownership (unchanged).

---

## File structure

**Backend - new files**
- `src/Diariz.Domain/Entities/FormulaRunStatus.cs` - enum `Generating=0, Ready=1, Failed=2` (append-only).
- `src/Diariz.Domain/Entities/SectionFormulaResult.cs` - new entity (Phase 2).
- `src/Diariz.Api/Services/FormulaRunProcessor.cs` - static processor + shared run helpers.
- `src/Diariz.Api/Services/FormulaRunWorker.cs` - `BackgroundService` (verbatim shape of `SectionSummaryWorker.cs`).
- `src/Diariz.Api/Controllers/SectionFormulaResultsController.cs` - section results CRUD/email/download (Phase 2).
- Two EF migrations (Phase 1: `AddFormulaResultStatus`; Phase 2: `AddSectionFormulaResults`).

**Backend - modified**
- `src/Diariz.Domain/Entities/FormulaResult.cs` (+`Status`,+`Error`); `DiarizDbContext.cs` (config + DbSet).
- `src/Diariz.Api/Contracts/WorkerContracts.cs` (+`FormulaRunJob`).
- `src/Diariz.Api/Configuration/AppOptions.cs` (+`FormulaRunOptions`).
- `src/Diariz.Api/Services/JobQueue.cs` (+`EnqueueFormulaRunAsync`).
- `src/Diariz.Api/Services/FormulaRunner.cs` - refactor LLM/context logic into shared helpers; keep a sync
  runner for the MCP tool.
- `src/Diariz.Api/Controllers/FormulasController.cs` (`Run` -> async enqueue) and a new section run action.
- `src/Diariz.Api/Controllers/FormulaResultsController.cs` (list returns `Status`/`Error`; get-text guards).
- `src/Diariz.Api/Contracts/ApiDtos.cs` (+`Status`/`Error` on `FormulaResultDto`; +`SectionFormulaResultDto`).
- `src/Diariz.Api/Hubs/TranscriptionHub.cs` (+`NotifyFormulaStatusAsync`).
- `src/Diariz.Api/Program.cs` (register options, worker, hosted service).

**Frontend - modified**
- `apps/web/src/lib/types.ts` (`FormulaResult` +`status`/`error`; +`SectionFormulaResult`).
- `apps/web/src/lib/api.ts` (recording run returns pending; +section formula funcs).
- `apps/web/src/components/FormulasManager.tsx` (render `Generating`/`Failed` rows).
- `apps/web/src/components/FormulasPanel.tsx` (`ResultView` handles non-`Ready`; accept optional section source).
- `apps/web/src/pages/RecordingDetail.tsx` (poll `formula-results` while any `Generating`).
- `apps/web/src/pages/SectionDetail.tsx` (new Formulas tab + state + poll) (Phase 2).
- `apps/web/src/components/FormulaRunModal.tsx` (accept a section target) (Phase 2).
- i18n `apps/web/src/locales/{en,de,es,fr}/workspace.json` (a few folder-formula keys).

---

## PHASE 1 - Convert recording formula runs to async

**Ships:** recording Formulas run in the background with a `Generating` -> `Ready`/`Failed` lifecycle. This is
a functional change (async behavior + status) -> **Minor bump** on merge. Server redeploy only.

### Task 1.1: `FormulaRunStatus` enum
**Files:** Create `src/Diariz.Domain/Entities/FormulaRunStatus.cs`

- [ ] **Step 1:** Write the enum (mirror `SectionGenerationStatus.cs`, but a result row only exists once a run
  starts, so no `Idle`):
```csharp
namespace Diariz.Domain.Entities;

/// Lifecycle of an async formula run's result row. Append only - values persist as ints.
public enum FormulaRunStatus
{
    Generating = 0, // job enqueued, LLM not finished
    Ready = 1,      // Text populated
    Failed = 2      // run errored (see Error)
}
```
- [ ] **Step 2:** `dotnet build Diariz.slnx` - expected: succeeds. Commit.

### Task 1.2: `FormulaResult` gains `Status`/`Error` (failing test first)
**Files:** Modify `src/Diariz.Domain/Entities/FormulaResult.cs`; Test `tests/Diariz.Api.Tests/FormulaResultEntityTests.cs` (new, or fold into an existing FormulaResult test)

- [ ] **Step 1:** Write a failing unit test asserting a new `FormulaResult` defaults to `Generating` and
  round-trips `Error`:
```csharp
[Fact]
public void NewFormulaResult_DefaultsToGenerating()
{
    var r = new FormulaResult();
    Assert.Equal(FormulaRunStatus.Generating, r.Status);
    Assert.Null(r.Error);
}
```
- [ ] **Step 2:** Run it: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~FormulaResultEntity` - expected: FAIL (no `Status` member).
- [ ] **Step 3:** Add to `FormulaResult.cs`: `public FormulaRunStatus Status { get; set; } = FormulaRunStatus.Generating;` and `public string? Error { get; set; }`.
- [ ] **Step 4:** Re-run - expected: PASS.
- [ ] **Step 5:** Commit.

### Task 1.3: EF migration `AddFormulaResultStatus`
**Files:** `dotnet ef migrations add AddFormulaResultStatus --project src/Diariz.Domain --startup-project src/Diariz.Api`

- [ ] **Step 1:** Generate the migration. Inspect it: it must add `Status integer NOT NULL` (default `0`) and
  `Error text NULL` to `FormulaResults` (pattern: `20260709115124_AddSectionSummaryAndMinutes.cs`).
- [ ] **Step 2:** **Backfill existing rows to `Ready`** - existing results are all completed. In the migration
  `Up`, after `AddColumn`, add: `migrationBuilder.Sql("UPDATE \"FormulaResults\" SET \"Status\" = 1;");`
  (1 = `Ready`). New rows still default to `Generating` (set in code at enqueue).
- [ ] **Step 3:** `dotnet build Diariz.slnx` - expected: succeeds.
- [ ] **Step 4:** **Forward-restore check:** adding columns is additive and forward-restore-safe (an older dump
  lacks them; restore migrates up and the backfill sets `Ready`). **No `MaintenanceController.CurrentFormat`
  bump.** Note this in the eventual PR.
- [ ] **Step 5:** Commit.

### Task 1.4: `FormulaRunJob` record + `FormulaRunOptions` + `IJobQueue.EnqueueFormulaRunAsync`
**Files:** Modify `src/Diariz.Api/Contracts/WorkerContracts.cs`, `src/Diariz.Api/Configuration/AppOptions.cs`, `src/Diariz.Api/Services/JobQueue.cs`; Test `tests/Diariz.Api.IntegrationTests/RedisJobQueueIntegrationTests.cs`

- [ ] **Step 1 (test-first, integration):** Add a test mirroring the existing section-job enqueue test: enqueue
  a `FormulaRunJob` and assert one entry lands on the `formula-run-jobs` stream with a `"job"` field
  deserializing back to the same record. Run it - expected: FAIL (no method).
- [ ] **Step 2:** In `WorkerContracts.cs` add (note: **one** unified record; `RecordingId` XOR `SectionId`, the
  other null; `ResultId` is the pre-created row to flip; `UserId` for per-user LLM config resolution):
```csharp
public record FormulaRunJob(Guid? RecordingId, Guid? SectionId, Guid ResultId, Guid FormulaId, Guid UserId);
```
- [ ] **Step 3:** In `AppOptions.cs` add `FormulaRunOptions` (mirror `SectionSummaryOptions`):
  `Section = "FormulaRun"`, `StreamKey = "formula-run-jobs"`, `ConsumerGroup = "formula-runners"`,
  `ConsumerName = "api-1"`, `CombineCharBudget = 32000` (reduce-step budget).
- [ ] **Step 4:** In `Program.cs` register `builder.Services.Configure<FormulaRunOptions>(config.GetSection(FormulaRunOptions.Section));` near the other section options (~lines 33-34).
- [ ] **Step 5:** In `JobQueue.cs` add to `IJobQueue`: `Task EnqueueFormulaRunAsync(FormulaRunJob job, CancellationToken ct = default);` and implement in `RedisJobQueue` (copy the 3-line `EnqueueSectionSummaryAsync` body against `_formulaRunOpts.StreamKey`; inject `IOptions<FormulaRunOptions>`).
- [ ] **Step 6:** Run the integration test (needs Docker) - expected: PASS. `dotnet build Diariz.slnx`. Commit.

### Task 1.5: Extract shared run helpers + `FormulaRunProcessor` (recording path)
**Files:** Modify `src/Diariz.Api/Services/FormulaRunner.cs`; Create `src/Diariz.Api/Services/FormulaRunProcessor.cs`; Test `tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs`

The current `FormulaRunner.RunAsync` (load recording w/ context-gated includes -> `BuildContextData` ->
`FormulaContextBuilder.Build` -> `IChatStreamClient.StreamAsync` drain -> persist) is split so the LLM/context
logic is reusable by both the async processor and the (still-sync) MCP tool runner.

- [ ] **Step 1 (test-first):** Write `FormulaRunProcessorTests` using `TestDb.Create()` + a fake chat stream
  client (see `tests/Diariz.Api.TestSupport`) + a fake settings resolver (Enabled=true). Seed a recording
  with a transcription/segments and a `FormulaResult` row in `Generating`. Assert `ProcessAsync` flips it to
  `Ready` with the fake's output text. Add a second test: a fake client that throws -> row becomes `Failed`
  with `Error` set. A third: resolver `Enabled=false` -> `Failed` "not configured". Run - expected: FAIL.
- [ ] **Step 2:** Create `FormulaRunProcessor` as a **static** class (mirror `SectionSummaryProcessor` for
  testability) with signature:
```csharp
public static async Task ProcessAsync(
    DiarizDbContext db, IChatStreamClient chat, ISummarizationSettingsResolver settings,
    ITranscriptionHub hub, FormulaRunJob job, int reduceCharBudget, ILogger logger, CancellationToken ct)
```
  Body: resolve `cfg = settings.ResolveAsync(job.UserId)`; if `!cfg.Enabled` -> load result row, set
  `Failed`/"not configured", save, notify, return. Load `formula` by `job.FormulaId`; if null -> `Failed`
  "formula removed". Then `try`: `job.RecordingId is { } recId` -> `text = await
  RunOverRecordingAsync(db, chat, cfg, formula, recId, ct)`; load the `FormulaResult` by `job.ResultId`
  (return if deleted), set `Text=text`, `Status=Ready`, `Error=null`; save; `hub.NotifyFormulaStatusAsync`.
  `catch` (distinguish LLM timeout via `!ct.IsCancellationRequested` as the current runner does): set
  `Failed`/`ex.Message`, save, notify. (Section branch added in Phase 2.)
- [ ] **Step 3:** Extract two shared statics (used by processor **and** the sync runner):
  - `BuildRecordingContextAsync(db, recordingId, FormulaContext flags, CancellationToken ct) -> string` -
    move `LoadRecordingAsync` includes + `LoadNoteLinesAsync` + `BuildContextData` + `FormulaContextBuilder.Build`
    here. **For the section path (Phase 2) this loads by id without an ownership filter**; the recording-run
    endpoint enforces ownership separately (Task 1.6), so keep the ownership check in the controller, not here.
  - `RunPromptAsync(IChatStreamClient chat, ResolvedConfig cfg, string systemPrompt, string userContext, CancellationToken ct) -> string` -
    the linked-CTS `CancelAfter(cfg.TimeoutSeconds)` + `StreamAsync` drain into a `StringBuilder`.
  - `RunOverRecordingAsync` = `BuildRecordingContextAsync` then `RunPromptAsync(chat, cfg, formula.Prompt, context, ct)`.
- [ ] **Step 4:** Refactor `FormulaRunner.RunAsync` (the **sync** path for the MCP tool) to call the same
  shared statics, then persist a `Ready` `FormulaResult` inline and return it. Keep its access check
  (`AccessibleBy(userId)` ownership) before building context.
- [ ] **Step 5:** Run the processor tests + existing `FormulaRunnerTests` - expected: PASS. `dotnet build Diariz.slnx`. Commit.

### Task 1.6: `FormulaRunWorker` background service
**Files:** Create `src/Diariz.Api/Services/FormulaRunWorker.cs`; register in `Program.cs`

- [ ] **Step 1:** Copy `src/Diariz.Api/Services/SectionSummaryWorker.cs` verbatim; rename to `FormulaRunWorker`;
  inject `IOptions<FormulaRunOptions>`; in `HandleEntryAsync` deserialize `FormulaRunJob` and dispatch to
  `FormulaRunProcessor.ProcessAsync(ctx, chatClient, resolver, hub, job, _opts.CombineCharBudget, _log, ct)`
  (resolve `IChatStreamClient` + `ISummarizationSettingsResolver` + `DiarizDbContext` + `ITranscriptionHub`
  from the per-job scope). Keep **XACK-in-`finally`** and the group `EnsureGroupAsync`.
- [ ] **Step 2:** `builder.Services.AddHostedService<FormulaRunWorker>();` in `Program.cs` near line 257.
- [ ] **Step 3:** `dotnet build Diariz.slnx` - expected: succeeds. Commit. (Worker loop itself is exercised by
  the integration test in Task 1.9; the processor is unit-tested in 1.5.)

### Task 1.7: `NotifyFormulaStatusAsync` on the hub
**Files:** Modify `src/Diariz.Api/Hubs/TranscriptionHub.cs`

- [ ] **Step 1:** Add an extension mirroring `NotifySectionStatusAsync`, emitting client event
  `FormulaResultStatusChanged` with `{ recordingId, sectionId, formulaResultId, status }` (recordingId or
  sectionId null as appropriate) to the owner's per-user group.
- [ ] **Step 2:** Build. Commit. (Frontend already refetches on poll; SignalR is a latency nicety - the poll
  in Task 1.10 is the source of truth.)

### Task 1.8: `FormulasController.Run` -> async enqueue (test-first)
**Files:** Modify `src/Diariz.Api/Controllers/FormulasController.cs`; Modify `tests/Diariz.Api.Tests/FormulasControllerTests.cs`

- [ ] **Step 1:** Update the existing Run test (and add one) to the async contract: `Run` returns `202 Accepted`
  with a `FormulaResultDto` whose `Status == "Generating"`, **creates** a `FormulaResult` row in `Generating`,
  and **enqueues** exactly one `FormulaRunJob` on the fake job queue (extend `FakeJobQueue` in TestSupport
  with a `FormulaRunJobs` list). Keep the access checks: not-owned/hidden -> 404; `!cfg.Enabled` -> 400. Run -
  expected: FAIL.
- [ ] **Step 2:** Rewrite `Run`: resolve the formula + enforce run access + `cfg.Enabled` (reuse the current
  guards, factored out of `FormulaRunner`); create a `FormulaResult { RecordingId, FormulaId, Name =
  formula.Name, CreatedByUserId = UserId, Ordinal = next, Status = Generating }`; `SaveChanges`; enqueue
  `new FormulaRunJob(recordingId, null, result.Id, formula.Id, UserId)`; `NotifyFormulaStatusAsync`; return
  `Accepted(FormulaResultDto…)` (origin resolved). Do **not** call `IFormulaRunner` here anymore.
- [ ] **Step 3:** Run the controller tests - expected: PASS. Commit.

### Task 1.9: DTO `Status`/`Error` + results-list/get-text guards (test-first)
**Files:** Modify `src/Diariz.Api/Contracts/ApiDtos.cs`, `src/Diariz.Api/Controllers/FormulaResultsController.cs`; Modify `tests/Diariz.Api.Tests/FormulaResultsControllerTests.cs`; Integration `tests/Diariz.Api.IntegrationTests`

- [ ] **Step 1:** Add `Status` (string) + `Error` (string?) to `FormulaResultDto`. Update `List` to project
  them. Guard `GET {id}` (text): if `Status != Ready` return the row's `Error`/empty appropriately (return
  `FormulaResultTextDto("")` for `Generating`; the `Error` message for `Failed`) - and a unit test for each.
- [ ] **Step 2 (integration, needs Docker):** End-to-end: enqueue via `Run`, run `FormulaRunProcessor` against
  the real Postgres row, assert the list now shows `Ready` + text. (Reuses `ContainersFixture`.)
- [ ] **Step 3:** Run unit + integration - expected: PASS. `dotnet build Diariz.slnx`. Commit.

### Task 1.10: Frontend - `Generating`/`Failed` in the recording Formulas tab (test-first)
**Files:** Modify `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`, `apps/web/src/components/FormulasManager.tsx`, `apps/web/src/components/FormulasPanel.tsx`, `apps/web/src/pages/RecordingDetail.tsx`; Tests `apps/web/src/components/FormulasManager.test.tsx` (+ RecordingDetail poll test)

- [ ] **Step 1:** `types.ts`: add `status: "Generating" | "Ready" | "Failed"` and `error: string | null` to
  `FormulaResult`. `api.runFormula` return type stays `FormulaResult` (now `Generating`).
- [ ] **Step 2 (test-first):** In `FormulasManager.test.tsx` add cases: a `Generating` result renders a
  spinner/"Generating..." affordance and is **not** selectable-to-view yet; a `Failed` result shows an error
  marker with its `error`. Use native RTL assertions (jest-dom is **not** installed - see project memory). Run
  - expected: FAIL.
- [ ] **Step 3:** `FormulasManager.tsx`: render per-`status` (Ready = current row; Generating = muted +
  spinner, click disabled; Failed = error tint + `error` tooltip). `FormulasPanel.tsx` `ResultView`: if the
  selected result is not `Ready`, show the Generating/Failed state instead of fetching text.
- [ ] **Step 4:** `RecordingDetail.tsx`: add `refetchInterval` to the `["formula-results", id]` query -
  `2500` while any result `status === "Generating"`, else `false` (copy the shape from
  `SectionDetail.tsx` summary/minutes poll). `FormulaRunModal.onRun` already selects the returned (now
  `Generating`) result id; the poll fills it in.
- [ ] **Step 5:** `npx vitest run src/components/FormulasManager.test.tsx` then the full web suite; `npm run build`. Expected: green. Commit.

### Task 1.11: Phase-1 release + docs
- [ ] **Step 1:** Bump `version.json` + 3 mirrors (Minor +1, Build ->0): `0.133.3` -> `0.134.0`.
- [ ] **Step 2:** Add `RELEASES[0]` in `apps/web/src/lib/releases.ts` (headline "Formula runs now run in the
  background", summary + `changed` bullets). No em/en dashes.
- [ ] **Step 3:** `docs/Overall_Synopsis_of_Platform.md`: document the new `formula-run-jobs` stream +
  `FormulaRunWorker` + async run pipeline (a new cross-boundary contract). `docs/Data_Schema.md`: add
  `FormulaResults.Status`/`.Error` + the migration-history row.
- [ ] **Step 4:** Full `dotnet test` + web suite green. Push branch, open PR. State: **server redeploy only**,
  no desktop release; not-forward-restore-unsafe so no `CurrentFormat` bump.

---

## PHASE 2 - Section/folder formulas (map-reduce) + folder Formulas tab

**Ships:** the requested feature. **Minor bump** on merge. Server redeploy only.

### Task 2.1: `SectionFormulaResult` entity + DbContext + migration
**Files:** Create `src/Diariz.Domain/Entities/SectionFormulaResult.cs`; Modify `DiarizDbContext.cs`; migration `AddSectionFormulaResults`

- [ ] **Step 1:** Entity (mirror `FormulaResult` but section-scoped):
```csharp
public class SectionFormulaResult
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }         public Section? Section { get; set; }
    public Guid? CreatedByUserId { get; set; }  public ApplicationUser? CreatedBy { get; set; }
    public Guid? FormulaId { get; set; }         public Formula? Formula { get; set; }
    public string Name { get; set; } = "";       // formula-name snapshot
    public string Text { get; set; } = "";
    public int Ordinal { get; set; }
    public FormulaRunStatus Status { get; set; } = FormulaRunStatus.Generating;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```
- [ ] **Step 2:** `DiarizDbContext.cs`: add `DbSet<SectionFormulaResult> SectionFormulaResults`; config
  mirroring `FormulaResult` (index `(SectionId, Ordinal)`; FK to `Section` **Cascade**; FK to `Formula`
  **SetNull**; FK to `CreatedBy` **SetNull**; `Name` max 256). Guard any Postgres-only config behind the
  provider check if needed (no pgvector here, so plain).
- [ ] **Step 3:** `dotnet ef migrations add AddSectionFormulaResults …`. Additive new table -> forward-restore
  safe, no `CurrentFormat` bump. `dotnet build Diariz.slnx`. Commit.

### Task 2.2: Section recording-set resolver + `FormulaRunProcessor` section branch (test-first)
**Files:** Modify `src/Diariz.Api/Services/FormulaRunProcessor.cs`; Test `tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs`

- [ ] **Step 1 (test-first):** Add tests for `RunOverSectionAsync`: (a) a section with 2 recordings ->
  processor calls the fake chat client **3 times** (2 map + 1 reduce) and persists the reduce output to the
  `SectionFormulaResult` row (`Ready`); (b) a section with **1** recording -> **1** call, result = that
  output (no reduce); (c) **0** recordings -> row `Failed` "no meetings" (or the enqueue guard covers this -
  assert whichever layer you choose; prefer the enqueue-time 400 in Task 2.3 and keep the processor robust);
  (d) a fake client throwing on the reduce -> `Failed`. Because the in-memory provider ignores ordering inside
  filtered includes, keep the recording-set query simple (top-level `Where`, not a filtered `Include`); if it
  needs real relational behavior, mark the unit `[Fact(Skip=…)]` and cover it in the integration task (2.7).
  Run - expected: FAIL.
- [ ] **Step 2:** Implement `RunOverSectionAsync(db, chat, cfg, formula, sectionId, reduceCharBudget, ct)`:
  - Resolve the recording set **room-aware, one level deep** (mirror `SectionPageController.IncludedSectionIdsAsync`
    + the `RoomRecordings` join): load `section` -> `roomId`; `includedSectionIds = {sectionId} ∪ (Sections
    where RoomId==roomId && ParentId==sectionId).Id`; `recordings = RoomRecordings where RoomId==roomId &&
    SectionId in includedSectionIds join Recordings, ordered by CreatedAt`.
  - **Map:** for each recording, `text_i = await RunOverRecordingAsync(db, chat, cfg, formula, rec.Id, ct)`
    (reuse Task 1.5 helper, **no per-recording persistence**). Skip recordings with no transcribable content
    for the flags (empty context -> skip, don't feed `EmptyContextFallback` into the reduce).
  - `if (map.Count == 0) throw new FormulaNoContentException(...)` (-> `Failed` "no meetings with content").
  - `if (map.Count == 1) return map[0].Text;`
  - **Reduce:** concat `## {meeting name}\n{text_i}` blocks up to `reduceCharBudget` with `(+N more meeting(s)
    omitted)` on overflow (mirror `FolderSummaryPrompt.JoinItems`); `return await RunPromptAsync(chat, cfg,
    formula.Prompt, combined, ct);`
  - In `ProcessAsync`, the `job.SectionId is { } secId` branch loads the `SectionFormulaResult` by
    `job.ResultId`, sets `Text`/`Ready`, notify; `catch` -> `Failed`.
- [ ] **Step 3:** Run tests - expected: PASS. `dotnet build Diariz.slnx`. Commit.

### Task 2.3: Section run endpoint (test-first)
**Files:** Modify `src/Diariz.Api/Controllers/FormulasController.cs` (add a section run action) or `SectionPageController.cs`; Tests in `tests/Diariz.Api.Tests`

- [ ] **Step 1 (test-first):** Assert `POST /api/sections/{sectionId}/formulas/{formulaId}/run`: non-member ->
  404 (hidden); `!cfg.Enabled` -> 400; **0 included recordings** -> 400 "no meetings"; success -> 202 +
  `SectionFormulaResultDto` (`Generating`), creates a `SectionFormulaResult` row + enqueues one `FormulaRunJob`
  with `SectionId` set / `RecordingId` null. Run - expected: FAIL.
- [ ] **Step 2:** Add the action (route `~/api/sections/{sectionId:guid}/formulas/{formulaId:guid}/run`,
  absolute override like the recording one). Gate with the section-page **membership** check
  (`ViewableSectionAsync`-equivalent; reuse `IRoomScope`/the SectionPageController helper). Resolve formula +
  run access + `cfg.Enabled`. Compute the included-recording **count** (reuse the resolver) and 400 if zero.
  Create `SectionFormulaResult { SectionId, FormulaId, Name, CreatedByUserId, Ordinal=next, Status=Generating }`;
  save; enqueue `new FormulaRunJob(null, sectionId, result.Id, formula.Id, UserId)`; notify; `Accepted(dto)`.
- [ ] **Step 3:** Run - expected: PASS. Commit.

### Task 2.4: Section results controller (list/get/update/delete/email/download) (test-first)
**Files:** Create `src/Diariz.Api/Controllers/SectionFormulaResultsController.cs`; DTO `SectionFormulaResultDto` in `ApiDtos.cs`; generalize `FormulaResultOrigins`; Tests

- [ ] **Step 1:** Add `SectionFormulaResultDto(Id, SectionId, Name, CreatedByUserId, CreatedAt, UpdatedAt,
  Status, Error, FormulaResultOriginDto Origin)`. Generalize `FormulaResultOrigins.ResolveAsync` to accept a
  lightweight projection `(Guid ResultId, Guid? FormulaId, Guid? CreatedByUserId)` so both result types reuse
  it (add an overload; keep the existing signature).
- [ ] **Step 2 (test-first):** Controller tests (route base `api/sections/{sectionId:guid}/formula-results`):
  **view** = section member; **edit/delete** = creator **or** room `ManageContents`. List (ordered
  Ordinal/CreatedAt, origins, Status/Error); get-text (guarded like Task 1.9); update (edit gate); delete (edit
  gate); email-to-self; download `.md`. Run - expected: FAIL.
- [ ] **Step 3:** Implement mirroring `FormulaResultsController` but section-scoped (membership via
  `IRoomScope.IsMemberAsync(UserId, section.RoomId)`, `ManageContents` for edit/delete). Reuse
  `MarkdownRenderer.ToHtml` + `MeetingMinutesEmail.BuildHtml` for email; `Slug(name)` for download filename.
- [ ] **Step 4:** Run - expected: PASS. `dotnet build Diariz.slnx`. Commit.

### Task 2.5: `api.ts` + `types.ts` section-formula surface (test-first where logic exists)
**Files:** Modify `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`

- [ ] **Step 1:** `types.ts`: `SectionFormulaResult` (like `FormulaResult` but `sectionId` instead of
  `recordingId`; includes `status`/`error`). Define a shared `FormulaResultLike` interface
  `{ id: string; name: string; createdAt: string; origin: FormulaResultOrigin; status: FormulaResult["status"]; error: string | null }`
  that both result types satisfy, so `FormulasManager`/`FormulasPanel` accept either.
- [ ] **Step 2:** `api.ts`: `runSectionFormula(sectionId, formulaId)`, `listSectionFormulaResults(sectionId)`,
  `getSectionFormulaResultText(sectionId, id)`, `updateSectionFormulaResult(sectionId, id, text)`,
  `deleteSectionFormulaResult(sectionId, id)`, `emailSectionFormulaResult(sectionId, id)`,
  `downloadSectionFormulaResult(sectionId, id)` - against `/api/sections/{id}/…`, mirroring the recording
  functions (reuse `triggerBlobDownload`). Commit (typecheck via `npm run build` in a later step).

### Task 2.6: Generalize `FormulaRunModal` + reusable panel for a section target (test-first)
**Files:** Modify `apps/web/src/components/FormulaRunModal.tsx`, `apps/web/src/components/FormulasPanel.tsx`; Tests

- [ ] **Step 1 (test-first):** `FormulaRunModal.test.tsx`: given a `target: { kind: "section"; sectionId }`
  prop, clicking a formula calls `api.runSectionFormula(sectionId, formulaId)` (not `runFormula`). Keep the
  existing recording behavior for `target: { kind: "recording"; recordingId }`. Run - expected: FAIL.
- [ ] **Step 2:** Refactor `FormulaRunModal` to take a `target` discriminator (default/existing =
  recording) and dispatch the run call accordingly; grouping/typeahead/Manage/FindShared unchanged.
  Make `FormulasPanel`'s `ResultView` fetch via an injected `loadText(id)` closure so it works for either
  source (recording or section).
- [ ] **Step 3:** Run modal tests + existing recording Formulas tests - expected: PASS.

### Task 2.7: Folder Formulas tab in `SectionDetail` (test-first) + integration
**Files:** Modify `apps/web/src/pages/SectionDetail.tsx`; i18n; Test `apps/web/src/pages/SectionDetail.test.tsx`; Integration `tests/Diariz.Api.IntegrationTests`

- [ ] **Step 1 (web test-first):** In `SectionDetail.test.tsx` assert: a Formulas tab renders; its results
  query (`["section-formula-results", id]`) drives `FormulasManager`; a `Generating` result shows the
  generating state and the query polls (mock `api.listSectionFormulaResults`); the Run button opens
  `FormulaRunModal` with a section target. Run - expected: FAIL.
- [ ] **Step 2:** Add the `formulas` `DetailTab` to `SectionDetail`'s `tabs` array (reuse `FormulasToolbar`,
  `FormulasManager`, `FormulasPanel`, `FormulaResultEditModal`, `SharedFormulasBrowser`, `PreferencesModal`).
  Lift the same state RecordingDetail uses (`selectedFormulaResultId`, `formulaRunOpen`, `editingFormulaResult`,
  `sharedBrowserOpen`, `managingFormulas`). Wire handlers to the `api.*SectionFormulaResult*` functions. Add
  `refetchInterval` (2500 while any result `Generating`) to the results query.
- [ ] **Step 3:** i18n: reuse all existing `formula*` keys; add only folder-specific copy if wording must
  differ (e.g. an empty-state line) across `en/de/es/fr`. No em/en dashes.
- [ ] **Step 4 (integration, needs Docker):** End-to-end section run: seed a room + section + 2 placed
  recordings with transcripts; `POST …/sections/{id}/formulas/{fid}/run`; run `FormulaRunProcessor`; assert the
  section result is `Ready` and the map-reduce called the chat client 3x (fake). Confirms the room-aware
  recording-set resolution against real Postgres (the piece the in-memory provider can't verify).
- [ ] **Step 5:** `npx vitest run` (full web) + `dotnet test` (unit + integration) - expected: green.
  `npm run build`. Commit.

### Task 2.8: Phase-2 release + docs (full checklist)
- [ ] **Step 1:** Bump `version.json` + 3 mirrors (Minor +1). `RELEASES[0]` entry (headline "Run a Formula
  over a whole folder", summary describing map-reduce + where results live; `added` bullets).
- [ ] **Step 2:** About-box `CAPABILITIES` **Formulas** row: extend to note formulas run over a folder (all its
  meetings) as well as a single recording. README **Features** row + `docs/features.md` bullet in lockstep.
- [ ] **Step 3:** `docs/Overall_Synopsis_of_Platform.md`: the section formula run pipeline (map-reduce) + the
  section run/result endpoints. `docs/Data_Schema.md`: the new `SectionFormulaResults` table + migration row.
- [ ] **Step 4:** Full `dotnet test` + web suite green. Push, open PR. State: **server redeploy only**, no
  desktop release; additive schema -> no `CurrentFormat` bump.

---

## Self-review notes
- **Spec coverage:** map-reduce (2.2), async-for-all (Phase 1 + 2.2/2.3), UI-only (no MCP section tool),
  results-on-section-page (2.7), context-applies-to-all-transcripts (2.2 map over included set). Covered.
- **Type consistency:** `FormulaRunJob(Guid? RecordingId, Guid? SectionId, Guid ResultId, Guid FormulaId,
  Guid UserId)` used identically in 1.4/1.8/2.3; `FormulaRunStatus{Generating,Ready,Failed}` used on both
  result entities and both DTOs; shared statics `BuildRecordingContextAsync`/`RunPromptAsync`/
  `RunOverRecordingAsync` named consistently across 1.5 and 2.2.
- **Open point for user review:** the MCP/chat `run_formula` tool stays **synchronous** (single recording,
  must return in-turn). Confirm that carve-out from "async for all runs" is acceptable.
- **Risk:** in-memory EF provider can't verify the room-aware recording-set query or ordering - those are
  pushed to integration tests (1.9, 2.7). Do not "fix" a skipped unit test by gaming the in-memory provider.

## Verification (whole feature)
- **.NET:** `FormulaRunProcessor` unit tests (recording + section map-reduce: 0/1/N recordings, failure,
  not-configured); controller tests (run enqueue + access gates, section results CRUD ownership); integration
  tests (real Postgres room-aware set + Redis `formula-run-jobs` round-trip + end-to-end run).
- **Web:** vitest for `FormulasManager` Generating/Failed, `FormulaRunModal` section target, `SectionDetail`
  Formulas tab + poll.
- **Live smoke (dev server first):** run a formula on a folder with 2-3 meetings -> a `Generating` row appears,
  resolves to `Ready`; open/edit/download/email it; confirm a sub-section run covers only its own meetings and
  a parent-folder run covers the sub-sections; confirm a non-member can't run.
