# Formulas - Phase 1 (Core) Implementation Plan

> **For agentic workers:** execute task-by-task with TDD (failing test first, watch it fail, minimal code to pass, commit). Steps use checkbox syntax. Full design context: `docs/superpowers/specs/2026-07-12-formulas-design.md`.

**Goal:** Ship the core of Formulas - saved prompts (Context + Prompt) run over a recording to produce named Markdown Results - with a new `ManageFormulas` permission, the `Formula`/`FormulaResult` data model, a synchronous run pipeline, the CRUD + run + results API, a new Formulas transcript tab (select-then-toolbar UX), and Personal formula management in Preferences. (Chat/MCP tool = Phase 2; admin popup = Phase 3.)

**Architecture:** Two new EF entities (`Formula`, `FormulaResult`) + migration; an `IFormulaRunner` that resolves per-user LLM config (`ISummarizationSettingsResolver`), assembles context, drives `IChatStreamClient` synchronously (the `ChatController.GenerateTitleAsync` pattern), and persists a result; a `FormulasController` (CRUD + run) and formula-results endpoints; a React Formulas tab reusing `DetailTabs`, `ActionsToolbar`/`ToolbarButton`, `MeetingMinutesEditModal`; and a Preferences Formulas section.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core/Postgres; React 19 + TS + vitest; xUnit (unit + Testcontainers integration).

**Version:** functional enhancement -> **Minor** bump (0.127.0 -> 0.128.0). Server redeploy only. One release entry.

**Conventions:** TDD; unit tests use the in-memory `TestDb` + fakes in `Diariz.Api.TestSupport` (no mocking lib); relational/cascade/pgvector behavior goes in the integration project. No em/en dashes in user-facing text. Build `Diariz.slnx` before pushing (catches integration-test compile breaks). Finish by pushing + opening a PR (main is branch-protected).

---

## Task 1: `ManageFormulas` permission plumbing

**Files:** `src/Diariz.Domain/Entities/PlatformPermission.cs`; `src/Diariz.Api/Program.cs`; `src/Diariz.Api/Services/Seeder.cs`; `apps/web/src/components/GroupsTab.tsx`; `apps/web/src/locales/{en,de,es,fr}/admin.json`; test `tests/Diariz.Api.Tests` (seeder).

- [ ] **Failing test first:** In `tests/Diariz.Api.Tests`, add a test that `Seeder.SeedGroupsAsync` grants `ManageFormulas` to both seeded groups (use the integration DB or a real-Npgsql context if the seeder needs it; if a unit test with in-memory works for `SeedGroupsAsync`, prefer that). Assert `Administrators` and `Platform Administrators` group `Permissions` include `PlatformPermission.ManageFormulas`. Run -> fails (flag doesn't exist).
- [ ] Add `ManageFormulas = 8` to `PlatformPermission` (append-only, after `ManagePlatform = 4`, with a doc comment "Create, edit and delete formulas.").
- [ ] `Program.cs`: add `o.AddPolicy("ManageFormulas", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageFormulas)));` beside the `ManageUsers` policy.
- [ ] `Seeder.SeedGroupsAsync`: add `| PlatformPermission.ManageFormulas` to BOTH `EnsureGroup` calls (Platform Administrators and Administrators). (Additive `|=` backfills existing deployments.)
- [ ] `GroupsTab.tsx`: add `{ bit: 8, key: "permManageFormulas" }` to `PERMISSION_BITS`.
- [ ] Add `"permManageFormulas"` to all four `admin.json`: en `"Manage formulas"`, de `"Formeln verwalten"`, es `"Gestionar fórmulas"`, fr `"Gérer les formules"`.
- [ ] Run the seeder test (pass), `dotnet build Diariz.slnx`, `cd apps/web && npm run build`. Commit: `feat: add ManageFormulas platform permission`.

---

## Task 2: `Formula` + `FormulaResult` entities + enums + DbContext + migration

**Files:** create `src/Diariz.Domain/Entities/Formula.cs`, `FormulaResult.cs`, `FormulaScope.cs`, `FormulaContext.cs`; modify `src/Diariz.Domain/DiarizDbContext.cs`; new migration under `src/Diariz.Domain/Migrations/`. Test: `tests/Diariz.Api.IntegrationTests`.

- [ ] **Failing integration test first** (`tests/Diariz.Api.IntegrationTests`, uses real Postgres): persist a `Formula` and a `FormulaResult` for a recording; assert round-trip, and that deleting the recording cascades its `FormulaResult`s, and deleting a `Formula` sets `FormulaResult.FormulaId` null (SET NULL). Run -> fails (types missing).
- [ ] Create `FormulaScope` enum (`Personal=0, Platform=1, Diariz=2`) with an append-only doc comment; `FormulaContext` `[Flags]` enum (`None=0, Transcript=1, Notes=2, Attachments=4, Summary=8, Minutes=16, Actions=32`).
- [ ] Create `Formula` entity per the spec (Id, Scope, OwnerUserId?+User, Name, Description?, Prompt, Context, Enabled, IsBuiltIn, CreatedAt/UpdatedAt).
- [ ] Create `FormulaResult` entity modeled on `MeetingNote` (Id, RecordingId+Recording, CreatedByUserId+CreatedBy, FormulaId?+Formula, Name, Text, Ordinal, CreatedAt/UpdatedAt).
- [ ] `DiarizDbContext`: add `DbSet<Formula>` + `DbSet<FormulaResult>`; in `OnModelCreating` configure `FormulaResult` -> `Recording` (WithMany, HasForeignKey `RecordingId`, `OnDelete(Cascade)`), `FormulaResult` -> `Formula` (`OnDelete(SetNull)`, `FormulaId` nullable), `FormulaResult` -> `ApplicationUser` (CreatedByUserId), and `Formula` -> `ApplicationUser` (OwnerUserId, optional). Mirror the `MeetingNote` config block.
- [ ] Create the migration: `dotnet ef migrations add AddFormulas --project src/Diariz.Domain --startup-project src/Diariz.Api`. Verify it creates both tables + FKs.
- [ ] Update `docs/Data_Schema.md` (two new tables + FKs/cascades + the migration-history row). Do NOT bump `MaintenanceController.CurrentFormat` (additive tables are forward-restore-safe - note this in the schema doc).
- [ ] Run the integration test (pass), `dotnet build Diariz.slnx`. Commit: `feat: add Formula and FormulaResult entities + migration`.

---

## Task 3: `IFormulaRunner` (synchronous run pipeline)

**Files:** create `src/Diariz.Api/Services/FormulaRunner.cs` (+ interface); create `src/Diariz.Api/Services/FormulaContextBuilder.cs` (pure context assembly). Test: `tests/Diariz.Api.Tests` with fakes.

Contract:
```csharp
public interface IFormulaRunner {
    // Throws FormulaAccessException (-> 403) / FormulaNotConfiguredException (-> 400) as appropriate.
    Task<FormulaResult> RunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct);
}
```
Behavior (mirror `ChatController.GenerateTitleAsync`):
1. Load formula + recording (highest-version transcription, speakers, summary, minutes, notes as needed). Enforce access: recording must be visible to `userId` (owner - reuse the existing `UserId` ownership filter used across recording endpoints); Personal formula requires `formula.OwnerUserId == userId`; Platform/Diariz require `Enabled`.
2. `cfg = await _settings.ResolveAsync(userId, ct)`; if `!cfg.Enabled` throw `FormulaNotConfiguredException`.
3. `FormulaContextBuilder.Build(formula.Context, recordingData)` -> the user-message string (transcript via `TranscriptFormatter.ToPlainText`; summary/minutes text; notes; skip attachments extraction in Phase 1 unless trivial - **decision: include Transcript/Summary/Minutes/Notes/Actions in Phase 1; Attachments extraction deferred to a later task with a `// TODO` and the flag still selectable but a no-op-with-note**). Keep the pure shaping in `FormulaContextBuilder` (unit-testable, no DB).
4. Messages `[system=formula.Prompt, user=context]`; drain `IChatStreamClient.StreamAsync(cfg, messages, ct)` into a `StringBuilder` under a `CancelAfter(cfg.TimeoutSeconds)` linked CTS.
5. Persist a `FormulaResult` (Name=formula.Name, Text=markdown, CreatedByUserId=userId, FormulaId=formula.Id, next Ordinal for the recording); return it.

- [ ] **Failing unit tests first** (`tests/Diariz.Api.Tests`, `TestDb` + a `FakeChatStreamClient` returning fixed markdown + a fake/stub `ISummarizationSettingsResolver`): (a) run persists a `FormulaResult` with the returned markdown + snapshot name; (b) a Personal formula owned by someone else throws access; (c) a disabled Platform formula throws access; (d) `!cfg.Enabled` throws not-configured. Add `FakeChatStreamClient` to `Diariz.Api.TestSupport`.
- [ ] Implement `FormulaContextBuilder` (pure) + unit tests for its shaping (each flag includes/excludes the right section; budget/truncation).
- [ ] Implement `FormulaRunner`; register in `Program.cs` (`AddScoped<IFormulaRunner, FormulaRunner>()`).
- [ ] Run tests (pass), `dotnet build Diariz.slnx`. Commit: `feat: add IFormulaRunner synchronous run pipeline`.

---

## Task 4: `FormulasController` - CRUD + run

**Files:** create `src/Diariz.Api/Controllers/FormulasController.cs`; DTOs in `src/Diariz.Api/Contracts/ApiDtos.cs` (`FormulaDto`, `CreateFormulaRequest`, `UpdateFormulaRequest`, `FormulaResultDto`). Test: `tests/Diariz.Api.Tests`.

Endpoints (`[Authorize]`, `UserId` from JWT):
- `GET /api/formulas` -> caller's Personal + all enabled Platform/Diariz (as `FormulaDto[]`).
- `POST /api/formulas` -> create. If `Scope==Personal` set `OwnerUserId=UserId`; if Platform/Diariz require `ManageFormulas` (check via `IUserPermissions.HasAsync(UserId, ManageFormulas)` -> 403 otherwise). `IsBuiltIn` only settable by the seeder (ignore from clients).
- `PUT /api/formulas/{id}` -> Personal: owner only; Platform/Diariz: `ManageFormulas`.
- `DELETE /api/formulas/{id}` -> Personal: owner; Platform: `ManageFormulas`; Diariz `IsBuiltIn`: 400 ("built-in formulas can't be deleted").
- `PUT /api/formulas/{id}/enabled` -> `ManageFormulas`; Platform/Diariz only.
- `POST /api/recordings/{recordingId}/formulas/{formulaId}/run` -> `IFormulaRunner.RunAsync`; return `FormulaResultDto`. Map `FormulaAccessException`->403, `FormulaNotConfiguredException`->400.

- [ ] **Failing tests first** (construct controller directly with `Http.Context(userId)`; use `TestDb`; a fake `IUserPermissions` + a fake `IFormulaRunner`): create-personal (any user, sets owner), create-platform-without-permission (403), create-platform-with-permission (ok), edit/delete personal by non-owner (403/404), delete Diariz built-in (400), list returns personal+enabled platform/diariz, run returns the result dto. Add a `FakeUserPermissions` + `FakeFormulaRunner` to `TestSupport` if not present.
- [ ] Implement the controller + DTOs.
- [ ] Build the whole solution (`dotnet build Diariz.slnx`) and fix any second construction sites. Run tests (pass). Commit: `feat: add FormulasController (CRUD + run)`.

---

## Task 5: Formula results collection endpoints

**Files:** add actions to `src/Diariz.Api/Controllers/RecordingsController.cs` (or a new `FormulaResultsController.cs` - prefer a dedicated controller `[Route("api/recordings/{recordingId:guid}/formula-results")]`). DTOs already added. Test: `tests/Diariz.Api.Tests`.

- `GET .../formula-results` -> list (anyone who can view the recording; Phase 1 = owner, since sharing surfaces results already via ownership; keep the "can view" check factored so rooms Just Work).
- `GET .../formula-results/{id}` -> `{ text }`.
- `PUT .../formula-results/{id}` -> update Markdown; **creator or recording owner** only.
- `DELETE .../formula-results/{id}` -> creator or recording owner.
- `POST .../formula-results/{id}/email` -> email to self via `IEmailSender` + `MarkdownRenderer.ToHtml` (owner address only; mirror the minutes-email path in `RecordingsController`).
- `GET .../formula-results/{id}/download?format=md` -> `File(bytes, "text/markdown", "{name}.md")`.

- [ ] **Failing tests first:** list returns the recording's results; update/delete by creator ok; update/delete by an unrelated user 403/404; download returns the markdown bytes; email calls the fake `IEmailSender`. Reuse fakes.
- [ ] Implement. Build solution, run tests (pass). Commit: `feat: add formula-results endpoints (list/get/edit/delete/email/download)`.

---

## Task 6: Seed Diariz starter formulas

**Files:** `src/Diariz.Api/Services/Seeder.cs` (+ call site in `Program.cs` boot). Test: `tests/Diariz.Api.IntegrationTests` (or unit if feasible).

- [ ] **Failing test first:** after `SeedFormulasAsync`, the built-in formulas exist (Scope=Diariz, IsBuiltIn, Enabled), and a second call doesn't duplicate them (idempotent), and it does NOT overwrite an admin edit to a built-in's prompt (create-only).
- [ ] Add `SeedFormulasAsync(DiarizDbContext db)` with an `EnsureFormula(key,name,description,prompt,context)` create-only helper (find by a stable Name or a `Key`; **use Name as the stable key** since there's no key column - acceptable for seeds). Seed: **Follow-up email**, **Meeting recap**, **Decisions & risks**, **Tone & sentiment read** (sensible prompts + context flags). Call it from the boot sequence next to the other seeders in `Program.cs`.
- [ ] Run test (pass), build. Commit: `feat: seed Diariz-provided starter formulas`.

---

## Task 7: Web API client + types

**Files:** `apps/web/src/lib/api.ts`; `apps/web/src/lib/types.ts`.

- [ ] Add types: `FormulaScope` (union `"Personal"|"Platform"|"Diariz"` - the enum serialises to its name via the global `JsonStringEnumConverter`), `Formula` (id, scope, name, description, prompt, context:number, enabled, isBuiltIn, ownerUserId?), `FormulaResult` (id, recordingId, name, createdByUserId, createdAt, updatedAt), and a `FormulaResultText`. **Declare `context` as `number`** (the `[Flags]` field would otherwise arrive as a string - known gotcha).
- [ ] Add api methods: `listFormulas`, `createFormula`, `updateFormula`, `deleteFormula`, `setFormulaEnabled`, `runFormula(recordingId, formulaId)`, `listFormulaResults(recordingId)`, `getFormulaResult(recordingId,id)`, `updateFormulaResult`, `deleteFormulaResult`, `emailFormulaResult`, `downloadFormulaResult` (reuse `triggerBlobDownload`). Mirror the attachments + meeting-minutes methods.
- [ ] `npm run build` (typecheck) passes. Commit: `feat: web api client + types for formulas`.

---

## Task 8: Formulas transcript tab (run + results + select-toolbar + edit/download/email/delete)

**Files:** create `apps/web/src/components/FormulasManager.tsx`, `FormulasToolbar.tsx`, `FormulaRunModal.tsx`, `FormulaResultEditModal.tsx`, a `FlaskIcon` (inline SVG matching the codebase icon style, from `images/formula-icon.svg`); modify `apps/web/src/pages/RecordingDetail.tsx`; i18n `workspace` locales (4). Tests: vitest.

- [ ] **Failing tests first** (vitest + RTL): `FormulasToolbar` gates Open/Edit/Download/Email/Delete on exactly-one selection and Run is always enabled; `FormulasManager` renders results and single-selects on click; `FormulaRunModal` type-ahead lists available formulas grouped by scope and calls `runFormula` on pick, and shows the "Manage formulas" footer link. Mirror `ActionsToolbar`/`AddMemberTypeahead` test styles.
- [ ] Implement `FormulaRunModal` (type-ahead over `listFormulas`, grouped Diariz/Platform/Personal; footer "Manage formulas" link that opens Preferences->Formulas via the existing preferences-open mechanism/`initialTab="formulas"`).
- [ ] Implement `FormulaResultEditModal` by copying `MarkdownAttachmentEditModal.tsx` (load=`getFormulaResult`, save=`updateFormulaResult`); read-only viewers get `renderMarkdown` + Download/Email, no Save.
- [ ] Implement `FormulasManager` (results list + **local** single-select state, per the segment select-mode; not global `useSelection`) and `FormulasToolbar` (Run… / Open / Edit / Download / Email / Delete + count badge; icons via `ToolbarButton`).
- [ ] Wire into `RecordingDetail.tsx`: a `useQuery(["formula-results", id])`, a `refreshFormulas` invalidator, and push the `{ key:"formulas", label, toolbar, content }` tab into `detailTabs`. Add `detailTabFormulas` + toolbar/list/empty i18n keys to all 4 `workspace` locales (no em/en dashes).
- [ ] `npm test` + `npm run build` green. Commit: `feat: add Formulas transcript tab (run, results, select-toolbar)`.

---

## Task 9: Personal formula management (Preferences -> Formulas)

**Files:** `apps/web/src/components/PreferencesModal.tsx` (add tab `"formulas"`); create `apps/web/src/components/FormulasSection.tsx` + `FormulaEditModal.tsx`; i18n `account` locales (4). Tests: vitest.

- [ ] **Failing tests first:** `FormulasSection` lists the user's Personal formulas and opens the editor to create/edit; the editor captures Name/Description/Context (checkboxes)/Prompt and calls `createFormula`/`updateFormula`; delete confirms. Mirror `AiSettingsSection` test style.
- [ ] Implement `FormulaEditModal` (fields: Name, Description, Context checkboxes over the flag bits, Prompt textarea) and `FormulasSection` (list + create/edit/delete over `listFormulas` filtered to `scope==="Personal"`).
- [ ] Add the `"formulas"` `PreferencesTab` + entry (label `t("account:tabFormulas")`) and render `<FormulasSection/>`; ensure `initialTab="formulas"` works (Task 8's link relies on it).
- [ ] i18n `account` keys (4 locales). `npm test` + `npm run build` green. Commit: `feat: add Personal formulas management in Preferences`.

---

## Task 10: Version, release notes, docs

**Files:** `version.json` + 3 mirrors; `apps/web/src/lib/releases.ts`; `README.md`; `docs/features.md`; `docs/Overall_Synopsis_of_Platform.md`.

- [ ] Bump to **0.128.0** across `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`.
- [ ] Add `RELEASES[0]` (0.128.0) with headline "Run saved prompts over a recording with Formulas" + summary + `added` bullets (Formulas tab, personal formulas, Diariz starter formulas, ManageFormulas permission). Add a `CAPABILITIES` row "Formulas". `RELEASES[0].version` must equal `version.json`.
- [ ] README Features table row + `docs/features.md` prose bullet (Formulas) in lockstep. Update `docs/Overall_Synopsis_of_Platform.md` (Formula/FormulaResult entities, the run pipeline, `ManageFormulas`; note the chat/MCP tool is Phase 2). (`Data_Schema.md` already updated in Task 2.)
- [ ] `npx vitest run src/lib/releases.test.ts` + `npm run build` green. Commit: `chore: release 0.128.0 - Formulas (core)`.

---

## Task 11: Full sweep

- [ ] `dotnet build Diariz.slnx` (0 warnings); `dotnet test tests/Diariz.Api.Tests`; `dotnet test tests/Diariz.Api.IntegrationTests` (Docker); `cd apps/web && npm test && npm run build`. All green, output pristine.
- [ ] Live smoke (best-effort; needs a running stack): create a Personal formula in Preferences, run it from a recording's Formulas tab, see the Result, open/edit/download/email it. If no stack is available, rely on the unit/integration/component tests and note it.

---

## Deployment surface
**Server redeploy only** - no desktop-shell files touched. The MCP `run_formula` tool and the admin Manage Formulas popup are **Phase 2 / Phase 3** and out of scope here.
