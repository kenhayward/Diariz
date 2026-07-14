# Merging Formulas and Meeting-Minutes Templates (design)

> Merges the two generation subsystems into one. A **Formula** becomes the unit of generation, authored with
> the meeting-template's structured block model; a **Meeting Type** becomes presentation + selection, naming a
> **primary formula** that generates the minutes and **additional formulas** run in the same pipeline. Both
> permission models are unchanged. Delivered as **four independent PRs** (Phases 1-4), each its own branch ->
> PR -> CI (main is branch-protected), one release per PR, docs in lockstep.

## Context

Diariz has grown **two parallel systems that do the same thing**: take a saved instruction, assemble some
context from a recording, call an LLM, store Markdown.

| | Meeting-minutes template (`MeetingType`) | Formula |
|---|---|---|
| Authoring | **Structured**: sections of typed blocks (heading / boilerplate / field / prompt / hr), drag-and-drop editor | **Free text**: one prompt textarea |
| Context | Implicit - always the transcript | **Explicit**: a `[Flags]` bitmask (Transcript/Notes/Summary/Minutes/Actions) |
| Ownership | Room-scoped: `RoomId`/`UserId` null = Platform | Scope enum: Personal / Platform / Diariz, plus `Shared` + subscriptions |
| Built-ins | 8, defined in **C#** (`MeetingTypeSeeder.Std(...)`) | 4, defined as **markdown files** (`src/Diariz.Api/formulas/*.md`) |
| Run engine | `MeetingTypeMinutesGenerator` + 2 strategies + composer | `FormulaRunProcessor` + `FormulaContextBuilder` |
| Output | The recording's `MeetingMinutes` | A `FormulaResult` row |

The duplication is near-total (`FormulaContextBuilder` vs `PromptTranscript`, `FormulaRunner` vs
`MeetingTypeMinutesGenerator`, `BuiltInFormulaCatalog` vs `MeetingTypeSeeder`) - yet each has what the other
lacks. Formulas can't be structured; templates can't choose their context, or be shared.

**Target:** one authoring model, one run engine.

- A **formula** is the unit of generation, authored with the structured block model.
- A **meeting type** is presentation + selection: icon, colour, name, description, group - plus a
  **primary formula** (which generates the minutes) and a list of **additional formulas** run in the same
  pipeline, landing in the Formulas tab (which is otherwise unchanged).
- Meeting types keep **no prompts of their own**. Any accessible formula can be primary.
- **Both permission models stay exactly as they are.**

## Locked decisions

1. **Scope rule enforced at save time.** A **Platform** meeting type may reference only **Platform/Diariz**
   (enabled) formulas. A **Personal** meeting type may reference anything its owner can run (own Personal,
   subscribed Shared, or enabled Platform/Diariz). A violation is a **400 at save**, so a template can never
   be persisted in a state that would fail at generation time for other users.
   *(Why it matters: `FormulaRunner.EnsureCanRun` lets only the owner run a Personal formula, and minutes
   generate as the **recording owner** - so a Platform template pointing at someone's Personal formula would
   break minutes for everyone else.)*
2. **Formulas become fully structured.** `Formula.Prompt` is replaced by structured content. Every existing
   formula migrates to a single **headless** section holding one prompt block - byte-identical output.
3. **`MeetingType.Overview` stays, and is still injected** as the `MEETING CONTEXT` preamble ahead of the
   primary formula's prompts. This is what lets one generic formula be reused by several templates that frame
   it differently, and it is the "description" a template keeps.
4. **A re-run replaces that formula's previous result** rather than appending a second one.

---

## The load-bearing detail: headless sections

`MeetingTypeMinutesComposer` **always emits a heading** per section
(`new string('#', level) + " " + section.Title`). A formula's output today has **none**.

So migrating `Formula.Prompt` into a section would silently prepend an `# H1` to the output of every existing
formula and every built-in - a real regression hiding inside an otherwise mechanical migration.

**Fix: `level: 0` = no heading** (body only). `TemplateSection.Level` is already an int and `Validate` already
range-checks it (currently 1-3), so this is a contained, append-safe extension:

- `MeetingTypeMinutesComposer`: `if (section.Level <= 0)` emit the body alone; otherwise emit the heading as now.
- `TemplateContent.Validate`: allow `0..3`; `Title` is optional (and ignored) when `Level == 0`.
- Migration of an existing prompt:
  `{"sections":[{"level":0,"title":"","blocks":[{"kind":"prompt","text":"<the old Prompt>"}]}]}`

This is what makes "a formula is just a template" true **without changing a single existing output**.

---

## Data model

### `Formula` - gains structure, loses its prompt

| Change | Notes |
|---|---|
| **+ `ContentJson`** | `jsonb` (plain text under the in-memory provider - mirror the `MeetingType.ContentJson` `isNpgsql` guard in `DiarizDbContext`). The same `TemplateContent` shape. |
| **- `Prompt`** | Dropped after backfill. |

`Scope`, `OwnerUserId`, `Context`, `Enabled`, `Shared`, `IsBuiltIn` are **unchanged** - formula permissions
stay as they are.

### `MeetingType` - loses its prompts, gains formula pointers

| Change | Notes |
|---|---|
| **+ `PrimaryFormulaId`** | `uuid` null, FK -> `Formulas`, **`ON DELETE RESTRICT`**. Null = fall back to the seeded General minutes formula. |
| **- `ContentJson`** | Dropped after conversion. |
| Kept | `Icon`, `Color`, `Title`, `GroupName`, `Overview`, `Key`, `UserId`/`RoomId` - presentation, selection and permissions all unchanged. |

**Why RESTRICT, not SET NULL:** a Platform template silently losing its formula would degrade every user's
minutes with no signal. Deleting a referenced formula returns a **400 naming the templates that use it**; the
admin repoints first. The same guard applies to **disable** - a disabled formula can't be run, so a template
pointing at one is a broken template.

### `MeetingTypeFormulas` - the additional formulas (new join table)

| Column | Notes |
|---|---|
| `Id` | uuid PK |
| `MeetingTypeId` | FK -> `MeetingTypes`, **Cascade** |
| `FormulaId` | FK -> `Formulas`, **Cascade** - unlike the primary, an additional formula going away is not a broken template, so it just drops off the list |
| `Ordinal` | int, run order |

Unique index `(MeetingTypeId, FormulaId)`.

### `FormulaResult` / `SectionFormulaResult` - gain an edit flag

| Change | Notes |
|---|---|
| **+ `IsUserEdited`** | bool, default false. Set when the result's Markdown is hand-edited. Additive - forward-restore-safe. See "Re-running a formula" below. |

### Shared type rename

`MeetingTypeContent` -> **`TemplateContent`** (it now serves both). `TemplateSection` / `TemplateBlock` keep
their names. A mechanical rename across C# + `apps/web/src/lib/types.ts`.

---

## The unified run engine

A **minutes run is a formula run** with a template's framing.

```csharp
ITemplateRunner.RunAsync(
    Formula formula,               // supplies ContentJson + Context flags
    string overview,               // MeetingType.Overview, or the formula's Description when run standalone
    Func<string, string?> fields,  // date/time/title/attendees/duration/action_items/notes
    Guid recordingId, SummarizationRequestConfig cfg, int charBudget, CancellationToken ct)
  -> string  // Markdown
```

This is today's `MeetingTypeMinutesGenerator.GenerateAsync`, generalised:

1. Parse `formula.ContentJson` -> `TemplateContent`.
2. Build the **context**. See the correction below - the two pipelines assemble it differently, and that
   difference must not be papered over.
3. Pick the strategy from `PlatformSettings.MinutesGenerationMode` (**unchanged**): `SingleCall`
   (a `[[WRITE: …]]` skeleton, one call) or `PerSection` (one call per prompt block, 4-way parallel).
4. Preamble = `minutes-section-preamble.md` + the **note-steering block** when the context includes `Notes`
   and notes exist. (Preserves today's minutes behaviour, since the migrated minutes formulas declare `Notes`.)
5. **Enhanced-notes pre-pass** when `content.HasField("notes")` - unchanged, still degrades safely on failure.
6. Compose via **`MeetingTypeMinutesComposer`** - already generic (content + field resolver + prompt resolver),
   reused as-is bar the level-0 change.

> ### Correction (found while implementing Phase 1)
>
> This spec originally said the strategies should take the flag-assembled context from
> `FormulaContextBuilder`, and called that "the key generalisation". **That would have silently changed the
> minutes prompt.** The two pipelines render the transcript differently:
>
> | | Helper | Output | Budget |
> |---|---|---|---|
> | Minutes | `PromptTranscript.Build` | `Speaker: Text` | transcript-specific char budget |
> | Formulas | `TranscriptFormatter.ToPlainText` | `[mm:ss] Speaker: Text` | whole context capped at 48k |
>
> Routing minutes through the formula context builder would add an `[mm:ss]` stamp to **every line of every
> minutes transcript** and change the truncation rule - thousands of extra tokens on a long meeting, and a real
> change to what the model sees. That is not a re-plumbing; it is a prompt change, and it deserves its own
> review, not a free ride inside a refactor.
>
> **So: context assembly stays with the caller.** Minutes keep `PromptTranscript`; formulas keep
> `FormulaContextBuilder`. The unification is the content model, the composer, and the field substitution
> (`TemplateFields`) - all genuinely shared. Converging the transcript rendering is a deliberate follow-up, not
> part of the merge.
>
> **Consequently Phase 1 does not move minutes at all.** Minutes only *become* formula runs once a meeting type
> names a primary formula - which is Phase 2. Phase 1 is: a formula's body becomes a template, and a formula run
> composes it. That is a strictly smaller, safer change, and it keeps the byte-identical bar honest.

**Reuse, do not rewrite:** `MeetingTypeMinutesComposer`, both `IMeetingTypeMinutesStrategy` implementations,
`FormulaContextBuilder`, `NotesEnhancer`/`NotesComposer`, `PromptTemplateProvider`. `FormulaRunProcessor` keeps
its Redis/job/status shell and delegates its body to the runner. `MeetingTypeMinutesGenerator` collapses into
the runner.

### Minutes generation

`MeetingMinutesProcessor` resolves the type -> its `PrimaryFormulaId` (or the seeded General minutes formula)
-> `ITemplateRunner.RunAsync(primary, type.Overview, fields, …)` -> saves `MeetingMinutes` as now.

**Then** it enqueues one `FormulaRunJob` per **additional** formula, in `Ordinal` order. Those land as ordinary
`FormulaResult` rows in the Formulas tab, which needs **no change at all**.

- **Ordering:** additional formulas run *after* the minutes are saved, so one may legitimately declare
  `Context.Minutes` and read them.
- **Failure isolation:** an additional formula failing marks only its own `FormulaResult` `Failed`. The minutes
  are untouched, and a primary failure does not block the additional runs.

### Circularity guard

`FormulaContext.Minutes` on the **primary** formula would ask the minutes to read themselves. The runner
**ignores the `Minutes` bit when running as a primary**, and the meeting-type editor doesn't offer it.

### Re-running a formula replaces its previous result

Today every run **appends** a `FormulaResult`. With formulas now running automatically on every minutes
generation, that would pile up duplicates on any recording that is re-transcribed. So:

**A run of formula F over recording R replaces R's existing result for F** - matched on
`(RecordingId, FormulaId)`, most-recent if legacy duplicates exist - rather than inserting a second one. Same
rule for `SectionFormulaResult` on `(SectionId, FormulaId)`.

The run becomes an **upsert**: reuse the existing row, keeping its `Id` and `Ordinal` so the list doesn't
reshuffle and open deep-links stay valid; set `Status = Generating`, clear `Text`/`Error`, and let the worker
fill it in exactly as it does today.

**The hazard this creates, and the guard.** A result's Markdown is **user-editable**
(`FormulaResultEditModal` -> `PUT /formula-results/{id}`), so a blind replace would silently destroy a
hand-edited document. The codebase already solves precisely this for minutes, and we mirror it:

- **The automatic pipeline never clobbers an edited result.** An additional formula whose previous result has
  `IsUserEdited` is **skipped** - exactly as `MeetingMinutesProcessor` today refuses to overwrite hand-edited
  minutes (`if (transcription.MeetingMinutes is { IsUserEdited: true }) return;`).
- **An explicit manual run does replace it**, clearing the flag. The user asked for it. This mirrors
  `RecordingsController.ApplyMeetingType`, which clears `IsUserEdited` before regenerating the minutes.

**Deliberately NOT adding a unique index on `(RecordingId, FormulaId)`.** Enforcing it would require
de-duplicating existing rows on upgrade - and installs today may legitimately hold several results from the
same formula. Those are real user documents; the migration must not delete them. Legacy duplicates are left
alone and collapse naturally the next time that formula runs.

### Folder / section runs

`RunOverSectionAsync` keeps its map-reduce shape: **map** = run the content per recording; **reduce** = compose
once more with the joined per-meeting outputs as the context. Field blocks resolve to null in the reduce (there
is no single recording) and the composer already drops empty blocks, so headings and boilerplate are emitted
exactly once.

---

## Built-in content: one authoring format

Today: 4 formulas as markdown files, 8 templates as C#. Unify on the **file**.

Extend `BuiltInFormulaCatalog` so the body parses as a **template**, reusing the marker syntax
`SingleCallMinutesStrategy` already emits:

```markdown
---
name: General Meeting minutes
description: Neutral, professional minutes suitable for forwarding.
context: Transcript, Notes, Actions
---
# Meeting details
Date: {{date}}
Time: {{time}}
Attendees: {{attendees}}
Duration: {{duration}}

# Purpose
[[WRITE: State the purpose / context of the meeting in 1-2 lines.]]

# Discussion
[[WRITE: Summarise the discussion grouped by theme (not chronologically), concise and decision-oriented. Omit this section if there was no substantive discussion.]]

# Enhanced notes
{{notes}}

# Action items
{{action_items}}
```

Mapping: `#`/`##`/`###` -> a section at that level; content before any heading -> a **level-0** section;
`{{field}}` -> a field block; `[[WRITE: …]]` -> a prompt block; `---` -> an `hr`; anything else -> boilerplate.
Emit **no** `breakAfter`, so the composer's legacy rule applies - which is what the seeded templates rely on
today, so `Date: {{date}}` still renders on one line.

This retires `MeetingTypeSeeder`'s C# content DSL and makes every built-in reviewable in a PR diff.

**Ships out of the box:** the existing 4 formulas (converted to level-0 single-prompt files - identical output)
plus **8 new Diariz minutes formulas**, one per seeded template (`general`, `customer`, `cadence-call`,
`weekly-meeting`, `one-to-one`, `interview`, `town-hall`, `webinar`), carrying exactly the content those
templates ship today. The seeder links each seeded `MeetingType.PrimaryFormulaId` to its formula by `Key`.

---

## Conversion of existing data

A single migration, `MergeFormulasAndMeetingTypes`:

1. `Formulas` **+ `ContentJson`** (jsonb, default `{"sections":[]}`).
2. **Backfill** every formula: `ContentJson` = one level-0 section with one prompt block carrying `Prompt`.
3. `Formulas` **- `Prompt`**.
4. `MeetingTypes` **+ `PrimaryFormulaId`** (FK RESTRICT); create `MeetingTypeFormulas`;
   `FormulaResults`/`SectionFormulaResults` **+ `IsUserEdited`**.
5. **Convert every existing meeting type** into a formula preserving its scope *and any admin edits*:
   - Platform type (`RoomId`/`UserId` null) -> a **Platform** formula
   - Personal type -> a **Personal** formula owned by the same user
   - carrying the type's `ContentJson`, `Context = Transcript | Notes | Actions`, named `"<Title> minutes"`.

   Then set `PrimaryFormulaId`. *Converting the seeded 8 as well - rather than repointing them at the new
   Diariz built-ins - is deliberate: it preserves any edit an admin has made to a standard, which the seeder
   is careful never to clobber.*
6. `MeetingTypes` **- `ContentJson`**.

**This is destructive and a semantic reshape, so `MaintenanceController.CurrentFormat` must go 1 -> 2.** An
older backup cannot be rolled forward onto this schema; the fence must hard-reject it rather than restore a
database whose templates have no prompts and whose formulas have no content.

---

## API

- `FormulaDto` / `CreateFormulaRequest` / `UpdateFormulaRequest`: **`Prompt` (string) -> `Content`
  (`TemplateContent`)**, validated with the shared `TemplateContent.Validate()`.
- `MeetingTypeDto` / `MeetingTypeRequest`: **`Content` removed**; **`PrimaryFormulaId`** and
  **`AdditionalFormulaIds: Guid[]`** added. `Overview`, `Icon`, `Color`, `Title`, `GroupName` unchanged.
- `MeetingTypesController` create/update gain the **scope check** (decision 1) - 400 naming the offending formula.
- `FormulasController` delete + set-enabled gain the **in-use check** - 400 naming the templates.
- `meetingTypeIo.ts` (JSON import/export): a template now references formulas by **name + scope** rather than
  embedding a prompt; import resolves them and reports any it cannot.

**Callers that read `formula.prompt` today and must change:** `SharedFormulasBrowser.tsx` (its "View" toggle
renders the raw prompt - it must render the template instead), plus `FormulaDto`'s other consumers.

---

## Web UI

- **Extract `TemplateContentEditor`** from `ManageMeetingTypesModal.tsx` (the section/block editor, its
  drag-and-drop, and the `meetingTypeDraft.ts` kernel) into a shared component. It is already pure and
  well-tested - a move, not a rewrite.
- **`FormulaEditModal`**: the prompt `<textarea>` is replaced by `TemplateContentEditor`. The context
  checkboxes stay.
- **`ManageMeetingTypesModal`**: loses the content editor; gains a **primary formula** picker and an
  **additional formulas** multi-select, both filtered to what the template's scope may reference (decision 1).
  Icon, colour, title, group and overview all stay.
- The **Formulas tab, hub tile, run modal, shared browser and results** are otherwise **unchanged**.

---

## Delivery (each phase = one PR: TDD, one release, docs in lockstep, PR + CI)

1. **Structured formulas.** `TemplateContent` rename; level-0 sections; `Formula.ContentJson` + backfill; drop
   `Prompt`; generalise the strategies to take an assembled context; `FormulaRunProcessor` delegates to the
   runner. **Regression bar: every existing formula produces byte-identical text.**
2. **Templates point at formulas.** `PrimaryFormulaId` + `MeetingTypeFormulas`; the conversion migration;
   `CurrentFormat` 1 -> 2; minutes delegate to the runner; additional formulas enqueue after the minutes; the
   scope + in-use validators. Also **run-replaces-previous-result** + `IsUserEdited` on both result tables -
   that is what makes automatic per-generation runs tenable, so it belongs here.
3. **Built-in content as files.** The template markdown DSL in `BuiltInFormulaCatalog`; the 8 seeded minutes
   formulas; retire `MeetingTypeSeeder`'s C# DSL.
4. **UI.** Shared `TemplateContentEditor`; the formula editor; the meeting-type editor with its two pickers.

Docs, on the phases that change behaviour: `Data_Schema.md` (the column adds/drops, the join table, the
`CurrentFormat` bump); `Overall_Synopsis_of_Platform.md` (the unified run engine, the minutes->formula
pipeline); README Features row + `docs/features.md` bullet + About `CAPABILITIES` row (formulas are now
structured; a meeting type now drives a set of formulas).

## Pre-existing bugs found while mapping this (out of scope - separate fixes)

- **`/formula <name>` in chat prints an empty body.** `ChatPanel.runFormulaCommand` reads the result text
  immediately after the run endpoint - which, since runs went async, returns a `Generating` row with
  `Text = ""`. It never polls. (`apps/web/src/components/ChatPanel.tsx`)
- **MCP `run_formula` can't see subscribed shared formulas.** `RunFormulaTool`'s visibility predicate omits the
  `FormulaSubscriptions` branch that `FormulasController.List` has, so a formula added from the Shared browser
  is invisible to the tool. (`src/Diariz.Api/Tools/RunFormulaTool.cs`)
- **`FormulaResultStatusChanged` has no web subscriber.** The server pushes it on every status transition, but
  `apps/web/src/lib/signalr.ts` only listens for `RecordingStatusChanged` - so both detail pages poll at 2.5 s
  instead. Either the hub call is dead code or the listener was never landed.
- **`MeetingType` scoping is half-migrated.** `MeetingTypesController` and `SectionPageController` filter on
  `RoomId`, while `RecordingsController.ApplyMeetingType` and `MeetingTypeMinutesGenerator.ResolveTypeAsync`
  still filter on `UserId`. They agree only because `Create` dual-writes both. Phase 2 touches both paths -
  worth converging them there.

## Verification (of the eventual feature)

- **.NET**: unit + integration (Testcontainers). New coverage: composer level-0 sections; the markdown template
  parser; the scope validator and the in-use (delete/disable) guards; the conversion migration against real
  Postgres; the upsert + `IsUserEdited` skip rule.
- **Golden test (phase 1's regression bar):** every built-in formula and every seeded template produces
  byte-identical output before and after the merge.
- **Web**: vitest for the shared `TemplateContentEditor`, the formula editor, and the meeting-type editor's
  two pickers (including that they only offer formulas the template's scope may reference).
- **Live**: run a formula from the hub tile; apply a meeting type and confirm the minutes generate from its
  primary formula and the additional formulas appear in the Formulas tab; delete a referenced formula and get a
  clear 400; restore a Format-1 backup and confirm it is hard-rejected.
- **Re-run semantics, explicitly**: run the same formula twice and confirm there is still **one** result row
  (same id, same position); hand-edit a result, re-transcribe, and confirm the automatic run **left the edit
  intact**; then run that formula manually and confirm it **did** replace it.
