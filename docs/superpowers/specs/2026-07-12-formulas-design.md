# Formulas - design spec

**Date:** 2026-07-12
**Status:** Approved (design phase; implementation to follow in phases - see Delivery)
**Deployment surface (this spec PR):** none (docs + icon asset only; nothing wired yet).

## Context

Diariz chat can answer questions over a transcript, but there is no way to save a **reusable prompt + a chosen
context** and run it to produce a durable, named document (a follow-up email, a recap, a read on how the room
felt). **Formulas** add exactly that: a saved Formula (Context + Prompt) is **Run** over a recording to
produce a **Result** - a named Markdown document the user can open, edit, download, or email. Formulas run
from a new transcript tab and from a new MCP-exposed `run_formula` chat tool. This spec covers the data model,
access rules, run pipeline, API, chat/MCP tool, and the frontend, and proposes a phased build.

## Naming (locked - weave these everywhere)

| Concept | Word |
|---|---|
| Feature / library | **Formulas** |
| One saved item | a **Formula** = a **Context** + a **Prompt** |
| Types | **Diariz-provided**, **Platform-wide**, **Personal** |
| Action | **Run** |
| Output | a **Result** (named Markdown document) |
| Transcript tab | **Formulas** (hosts the Run control + the generated Results list) |
| Icon | flask + sparkles - `images/formula-icon.svg`, used for the tab, the Run button, the `/formula` menu entry, and the Manage Formulas menu item |

## Access model (confirmed decisions)

- **Three scopes** (`FormulaScope`): `Personal`, `Platform`, `Diariz`.
- **Ownership:** Personal has an `OwnerUserId`; Platform/Diariz have none.
- **Run:**
  - Personal formula -> only its **owner**, on recordings they can access.
  - Platform / Diariz formula (and `Enabled`) -> **any user**, on recordings they can access.
  - "Access to a recording" = owns it, or is a room member who can view it.
- **Manage (create/edit/delete):**
  - Personal -> the owner (Preferences -> Formulas).
  - Platform -> admins with the new **Manage Formulas** permission.
  - Diariz-provided -> seeded and `IsBuiltIn`; Manage-Formulas admins may **enable/disable and edit**, but **not delete** (re-seeded on deploy, like the protected system groups).
- **Results** (stored on the recording):
  - **View / download / email** -> anyone who can view the recording.
  - **Edit / delete** -> the Result's **creator or the recording owner**.
  - A shared viewer therefore sees a Personal formula's Result (name + Markdown) but cannot re-run it (the formula isn't theirs) and cannot edit it (not the creator/owner).

## Permission: `ManageFormulas` (new `PlatformPermission` bit)

Mirror `ManageUsers` exactly (verified reuse):
- `src/Diariz.Domain/Entities/PlatformPermission.cs` - append `ManageFormulas = 8` (append-only; DB stores the int).
- `src/Diariz.Api/Program.cs` - `o.AddPolicy("ManageFormulas", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageFormulas)));`. `UserPermissions`/`PermissionAuthorizationHandler` are flag-generic - **no change**.
- `apps/web/src/components/GroupsTab.tsx` - add `{ bit: 8, key: "permManageFormulas" }` to `PERMISSION_BITS`; add `permManageFormulas` to all four `locales/*/admin.json`.
- `src/Diariz.Api/Services/Seeder.cs` - OR `ManageFormulas` into the seeded `PlatformAdminsGroup` (and likely `AdminsGroup`) via `EnsureGroup` (additive `|=`, so existing deployments gain it without revoking anything).
- **No migration** (permissions are an int column). Expose the flag to the web (see Manage Formulas UI) so the menu item can be gated.

## Data model (2 new tables + 1 migration)

Follow the existing entity conventions in `src/Diariz.Domain/Entities/` and register in `DiarizDbContext`.

### `Formula`
```
Id (guid)
Scope (int enum FormulaScope: Personal=0, Platform=1, Diariz=2) - append-only
OwnerUserId (guid?, FK ApplicationUser) - set only for Personal
Name (string)
Description (string?)
Prompt (text - the instructions, Markdown)
Context (int [Flags] FormulaContext: Transcript=1, Notes=2, Attachments=4, Summary=8, Minutes=16, Actions=32)
Enabled (bool) - Platform/Diariz availability toggle (Personal always effectively on for its owner)
IsBuiltIn (bool) - Diariz-seeded; blocks delete
CreatedAt / UpdatedAt
```

### `FormulaResult` (model on `MeetingNote` + `MeetingMinutes`)
```
Id (guid)
RecordingId (guid, FK Recording, cascade)
CreatedByUserId (guid, FK ApplicationUser)
FormulaId (guid?, FK Formula, SET NULL on delete) - keep the Result if the formula is later removed
Name (string) - formula name snapshot at run time
Text (text - the generated Markdown body; inline column, like MeetingMinutes.Text)
Ordinal (int)
CreatedAt / UpdatedAt
```
- Add `DbSet`s + `modelBuilder.Entity<...>` config (copy the `MeetingNote` relationship config), and an EF migration under `src/Diariz.Domain/Migrations/` (follow `AddMeetingNotes`).
- Update `docs/Data_Schema.md` (two new tables + FKs/cascades). **No `MaintenanceController.CurrentFormat` bump** - adding tables is forward-restore-safe (old dumps simply lack them; restore migrates up).

## Run pipeline (synchronous - reuse, do not add a job queue)

New `IFormulaRunner` service, called by both the run endpoint and the `run_formula` tool:
1. Load the formula; enforce **run access** (recording access + Personal-owner rule + `Enabled`).
2. Resolve per-user LLM config via `ISummarizationSettingsResolver.ResolveAsync(userId)`; if `!Enabled`, return a clear "not configured" error (mirror chat/summarise).
3. Build the **Context** blob from the formula's `Context` flags, reusing the chat context assembly (`TranscriptFormatter.ToPlainText` for the transcript; the summary/minutes text; notes; attachment extraction via `IAttachmentExtractor`/`IUrlFetcher`), with the same char budget approach as `ChatContextBuilder`.
4. Messages `[ system = formula.Prompt, user = context blob ]`; drain `IChatStreamClient.StreamAsync(cfg, messages, ct)` into a string (**exact precedent: `ChatController.GenerateTitleAsync`**), wrapped in a `CancelAfter(cfg.TimeoutSeconds)` linked CTS.
5. Persist a `FormulaResult` (`Name = formula.Name`, `Text = markdown`, `CreatedByUserId`, next `Ordinal`); return it.

Synchronous is required because the **MCP tool must return its result in the tool call** (the MCP transport is stateless - no async channel), and it is the simplest fit for the tab. `IChatStreamClient` is the right primitive (`ISummarizationClient` is summary-shaped and wrong here).

## API (new controllers)

**Formulas** (`FormulasController`, `[Authorize]`):
- `GET /api/formulas` - formulas visible to the caller: their Personal + all enabled Platform/Diariz.
- `POST /api/formulas` - create. Personal for anyone; Platform/Diariz requires `[Authorize(Policy="ManageFormulas")]` on that path.
- `PUT /api/formulas/{id}` - edit. Personal -> owner; Platform/Diariz -> ManageFormulas (includes editing Diariz).
- `DELETE /api/formulas/{id}` - Personal -> owner; Platform -> ManageFormulas; Diariz `IsBuiltIn` -> 400.
- `PUT /api/formulas/{id}/enabled` - ManageFormulas; Platform/Diariz only.

**Run** (on the recording):
- `POST /api/recordings/{recordingId}/formulas/{formulaId}/run` -> runs via `IFormulaRunner`, returns `FormulaResultDto`.

**Results** (collection, like attachments):
- `GET /api/recordings/{recordingId}/formula-results` - anyone who can view the recording.
- `GET /api/recordings/{recordingId}/formula-results/{id}` - Markdown text.
- `PUT .../{id}` - update Markdown - creator or recording owner.
- `DELETE .../{id}` - creator or recording owner.
- `POST .../{id}/email` - email to self (`IEmailSender` + `MarkdownRenderer.ToHtml`, owner address only; mirror `emailMeetingMinutes`).
- `GET .../{id}/download?format=md` - blob download (web reuses `triggerBlobDownload`).

Add DTOs to `Contracts/ApiDtos.cs` (mirror `MeetingMinutesDto` + its request records). Results are a **separate collection endpoint** (not embedded in `RecordingDetailDto`), same as attachments.

## Chat tool + MCP: `run_formula`

- New `RunFormulaTool : IChatTool` (`src/Diariz.Api/Tools/`):
  - `Name = "run_formula"`, `Title = "Run formula"`, `Description = "Run a saved formula (a saved prompt + selected context) over a recording and save the Markdown result to it."`
  - `ParametersSchema`: `{ formula: string (name), recording_id: string (uuid, optional) }`.
  - `ReadOnly => false` (it persists a Result).
  - `ExecuteAsync`: resolve the formula **by name** among those visible to `ctx.UserId`; resolve the recording from `recording_id`; call `IFormulaRunner`; return a short confirmation + a Markdown link to the recording's Formulas tab.
- Register with **one line**: `builder.Services.AddScoped<IChatTool, RunFormulaTool>();`. MCP exposes it **automatically** via `DiarizMcpHandlers`/`McpToolProjection` because it takes explicit params (not `SelectedRecordingIds`) and isn't in `ExcludedToolNames`. (Confirmed by exploration.)
- **In-app `/formula <name>`:** a client slash command in the chat that resolves the **current** recording from chat context, runs it (same `IFormulaRunner` via the run endpoint), appends the Result to the thread, and refreshes the Formulas tab. Errors clearly if no recording is in context. The `IChatTool` is the MCP/agentic surface; the slash command is the deterministic in-app surface; both share `IFormulaRunner`.

## Frontend

### New "Formulas" transcript tab (`RecordingDetail.tsx` + `DetailTabs`)
Push a tab object `{ key: "formulas", label: t("workspace:detailTabFormulas"), toolbar: <FormulasToolbar/>, content: <FormulasManager/> }` into the `detailTabs` array. The tab uses the **select-then-toolbar** metaphor (per the request): a single toolbar pinned at the top acts on the selected Result.

- **`FormulasToolbar`** (model on `ActionsToolbar.tsx` + `ToolbarButton`): **Run formula…** (always enabled; flask+sparkles icon; opens the Run picker) · **Open** (1 selected) · **Edit** (1 selected; creator/owner only) · **Download** (1 selected) · **Email** (1 selected) · **Delete** (1 selected; creator/owner) · a selection-count badge. Use **local** single-select state (like the transcript segment select-mode in `RecordingDetail.tsx`), **not** the global `useSelection` (which the list/chat share).
- **`FormulasManager`** (model on `AttachmentsManager`): the **Generated** Results list; each row = Result **Name** + `Generated {when} · by {who} · from the "{formula}" formula`; click to select (single). Empty: **Nothing generated yet. Run a formula.**
- **Run picker** (`FormulaRunModal`, type-ahead like `AddMemberTypeahead`/`SpeakerAssign`): search + list of available formulas grouped **Diariz / Platform / Personal**; Run against the current recording; on success invalidate the results query and auto-select the new Result. A footer shortcut **"Manage formulas"** opens **Preferences -> Formulas** directly (via `PreferencesModal`'s existing `initialTab="formulas"`), so users can jump straight from running to creating/editing their own Personal formulas - improving discoverability of where formulas come from. (Admins with `ManageFormulas` additionally see a second link to the **Manage Formulas** admin popup for Platform/Diariz formulas.)
- **Result open/edit**: reuse `MeetingMinutesEditModal` via a `load`/`save` wrapper (**copy `MarkdownAttachmentEditModal.tsx`**). Read-only viewers (not creator/owner) get the rendered Markdown (`renderMarkdown`) with Download/Email but no Save.
- `api.ts`: `listFormulas`, `createFormula`, `updateFormula`, `deleteFormula`, `setFormulaEnabled`, `runFormula(recordingId, formulaId)`, `listFormulaResults`, `getFormulaResult`, `updateFormulaResult`, `deleteFormulaResult`, `emailFormulaResult`, `downloadFormulaResult` (reuse `triggerBlobDownload`).
- `types.ts`: `Formula`, `FormulaResult`, `FormulaScope`, `FormulaContext` (flags - declare the flags field as `number`, per the `[Flags]`-serialises-as-string gotcha).

### Personal formula management (Preferences -> Formulas)
New `PreferencesTab` `"formulas"` + `FormulasSection` (model on `AiSettingsSection`): list the user's Personal formulas; create/edit/delete. Editor fields: **Name**, **Description**, **Context** (checkboxes: Transcript / Notes / Attachments / Summary / Minutes / Actions), **Prompt** (Markdown textarea).

### Platform/Diariz management (Manage Formulas admin popup)
New `ManageFormulasModal` (model on `ManageUsersModal`), opened from the account menu, **gated on the `ManageFormulas` permission** (expose it to the web like `isAdmin`). Lists Platform + Diariz formulas; create/edit Platform; edit + enable/disable Diariz (no delete when `IsBuiltIn`). Same Formula editor with a scope indicator.

## Seeding (Diariz-provided formulas)
Seed a small starter set in `Seeder.cs` (idempotent `EnsureFormula` by a stable key; **create-only**, never overwrite admin edits - like the group backfill): e.g. **Follow-up email**, **Meeting recap**, **Decisions & risks**, **Tone & sentiment read**. `Scope=Diariz`, `IsBuiltIn=true`, `Enabled=true`.

## i18n
Add keys in lockstep across `en/de/es/fr`: `admin.permManageFormulas`; `workspace.detailTabFormulas` + the Formulas toolbar/list/empty strings; the Preferences Formulas section + Manage Formulas modal strings. **No em/en dashes.**

## Delivery

### This spec PR
- Branch `feat/formulas`; adds `images/formula-icon.svg` + this spec. Docs/asset-only: **no version bump**, deployment surface **none** (nothing wired). `main` is branch-protected, so this goes via PR.

### Implementation phases (each a separate PR: TDD, one release, docs in lockstep, PR + CI)
1. **Core** - `ManageFormulas` permission; `Formula` + `FormulaResult` entities + migration; `IFormulaRunner` (sync); run + results endpoints; the **Formulas transcript tab** (Run picker, Results list, select-toolbar, open/edit/download/email/delete); **Personal** formula management in Preferences; seed the Diariz starter formulas. Functional enhancement -> Minor bump; server redeploy.
2. **Chat + MCP** - `run_formula` `IChatTool` (MCP-exposed) + the `/formula <name>` slash command. Update `Overall_Synopsis_of_Platform.md` (new tool + pipeline).
3. **Admin** - the **Manage Formulas** admin popup for Platform/Diariz formulas + enable/disable + exposing the permission to the web.

On the first user-facing phase: README Features row + `docs/features.md` bullet + About `CAPABILITIES` row (new "Formulas" capability); `Overall_Synopsis_of_Platform.md` (entities, run pipeline, MCP tool, `ManageFormulas`); `Data_Schema.md` (the two tables).

## Verification (of the eventual feature)
- **.NET**: unit + integration tests for `IFormulaRunner` access rules (Personal-owner, Enabled, recording access), permission gating on the management endpoints, and Results-CRUD ownership (creator/owner edit/delete; viewer read-only). No worker involved.
- **Web**: vitest for the Formulas tab (select-toolbar gating), the Run picker type-ahead, and the result editor wrapper.
- **Live**: run a formula on a recording -> Result appears; open/edit/download/email it; run via `/formula` in chat; run via the MCP `run_formula` tool from the Claude connector; confirm a shared viewer sees a Personal formula's Result but has no Run/Edit for it.
