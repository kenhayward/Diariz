# Implementation plan: merging Formulas and Meeting-Minutes Templates

> Executes the design in
> [`specs/2026-07-14-formulas-minutes-merge-design.md`](../specs/2026-07-14-formulas-minutes-merge-design.md).
> **Five PRs.** Each is its own branch -> PR -> CI (main is branch-protected), TDD, one release, docs in
> lockstep. Each leaves the app working and shippable - no phase depends on a later one to be correct.
>
> **Phase 0 is done** (shipped with this plan): the `/formula` empty-body bug.

## The through-line

Every phase is measured against one bar: **existing content keeps producing byte-identical output.** The merge
is a re-plumbing, not a behaviour change. Where behaviour *does* change (re-runs replace; templates reference
formulas) it is called out explicitly and tested.

The riskiest step is not the code - it is the **conversion migration** (Phase 2), which is destructive. It gets
its own integration tests against real Postgres, and it is the only phase that bumps the backup format.

---

## Phase 0 - fix `/formula <name>` printing an empty body *(done)*

**Why it's here and not left as a footnote:** the merge makes formulas run automatically on every minutes
generation, so the run lifecycle stops being a corner of the app and becomes core. This bug is a symptom of
that lifecycle being half-understood - the run went async in 0.134.0 and this caller was never updated.

- **The bug.** `ChatPanel.runFormulaCommand` called `api.runFormula` (which since the async change returns a
  **202 with a `Generating` row and `Text: ""`**) and then *immediately* read the result text. So
  `/formula <name>` printed `Ran the "X" formula:` followed by nothing, forever - it never polled.
- **The fix.** A new pure `lib/formulaRun.ts` -> `awaitFormulaResult(resultId, poll, { intervalMs, timeoutMs, sleep })`,
  polling until the row leaves `Generating`. Four outcomes, each handled: `ready` (fetch and print the text),
  `failed` (print the worker's reason), `gone` (the row was deleted mid-run), `timeout` (say it's still running
  and will appear in the Formulas tab - the run is **not** cancelled).
- **The test that mattered.** The existing ChatPanel test *mocked `runFormula` to return a row with no `status`
  at all* and `getFormulaResultText` to return a body - a fake more optimistic than the real API, which is
  exactly why the bug shipped. The fake now returns `Generating`, and two regression tests were added
  (waits-for-the-worker, surfaces-the-failure).

**Not fixed here, deliberately** (they're independent, and are listed in the spec): the missing
`FormulaResultStatusChanged` web subscriber, and MCP `run_formula` not seeing subscribed shared formulas.

---

## Phase 1 - Structured formulas

**Goal:** a formula is authored as structured content. **Nothing about its output changes.**

1. **Rename** `MeetingTypeContent` -> `TemplateContent` (C# + `apps/web/src/lib/types.ts`). Mechanical; no
   behaviour.
2. **Level-0 (headless) sections.** `MeetingTypeMinutesComposer`: `if (section.Level <= 0)` emit the body with
   no heading. `TemplateContent.Validate`: allow `0..3`, `Title` optional when level 0.
   *This is the load-bearing change - see the spec. Without it, every migrated formula silently gains an `# H1`.*
3. **`Formula.ContentJson`** (jsonb; mirror the `isNpgsql` guard used for `MeetingType.ContentJson`).
   Migration backfills each formula to one level-0 section holding one prompt block carrying `Prompt`, then
   **drops `Prompt`**.
4. **Generalise the strategies.** `PerSectionMinutesStrategy` / `SingleCallMinutesStrategy` currently hard-code
   the transcript as the user message. They take an **assembled context string** instead, produced by the
   existing `FormulaContextBuilder.Build(flags, …)`.
5. **`ITemplateRunner`** - `MeetingTypeMinutesGenerator.GenerateAsync` generalised (parse content -> build
   context -> pick strategy -> preamble + note-steering -> notes pre-pass -> compose).
   `FormulaRunProcessor` keeps its Redis/job/status shell and delegates its body to it.

**Reused, not rewritten:** `MeetingTypeMinutesComposer`, both strategies, `FormulaContextBuilder`,
`NotesEnhancer`/`NotesComposer`, `PromptTemplateProvider`.

**Tests (written first).** Composer: a level-0 section emits no heading; a level-1 still does. `TemplateContent`:
level 0 valid, title optional there, 4 rejected. **The regression bar:** a golden test asserting each of the 4
built-in formulas and 8 seeded templates produces byte-identical output before/after. Migration backfill tested
in integration (real Postgres, real jsonb).

**Web:** `FormulaDto.Prompt` -> `Content`, so `SharedFormulasBrowser`'s "View" toggle must render the template
rather than a raw prompt string. Editor UI is Phase 4 - here the formula editor keeps working against a
single-prompt-block content shape.

**Release:** functional enhancement -> **minor**. Additive migration -> **no `CurrentFormat` bump**. Server redeploy.

---

## Phase 2 - Meeting types point at formulas *(the destructive one)*

**Goal:** a meeting type stops owning prompts and starts naming formulas.

1. **Schema.** `MeetingType.PrimaryFormulaId` (FK **RESTRICT**); `MeetingTypeFormulas` join table
   (`MeetingTypeId` Cascade, `FormulaId` Cascade, `Ordinal`, unique on the pair);
   `FormulaResults`/`SectionFormulaResults` **+ `IsUserEdited`**.
2. **The conversion migration.** For every existing meeting type, create a formula preserving its scope *and any
   admin edits*: Platform type -> a **Platform** formula; Personal type -> a **Personal** formula owned by the
   same user; carrying its `ContentJson`, `Context = Transcript | Notes | Actions`, named `"<Title> minutes"`.
   Set `PrimaryFormulaId`. Then **drop `MeetingType.ContentJson`**.
   *The seeded 8 are converted too, rather than repointed at the new built-ins - so an admin's edit to a
   standard survives, which the seeder is already careful never to clobber.*
3. **`MaintenanceController.CurrentFormat` 1 -> 2.** Destructive drops + a semantic reshape; an older backup
   must be hard-rejected, not restored into a schema where templates have no prompts and formulas no content.
4. **Minutes generation** delegates to `ITemplateRunner` with the primary formula + `type.Overview`; the
   **`Minutes` context bit is ignored for a primary** (it would ask the minutes to read themselves). Then it
   enqueues one `FormulaRunJob` per additional formula, in `Ordinal` order, **after** the minutes are saved (so
   an additional formula may legitimately read them).
5. **Re-runs replace.** A run of F over R upserts R's existing result for F (keeping `Id` and `Ordinal`, so the
   list doesn't reshuffle and deep-links survive) rather than appending. **The automatic pipeline skips a
   result with `IsUserEdited`** - mirroring `MeetingMinutesProcessor`'s existing refusal to overwrite edited
   minutes - while an **explicit manual run replaces it and clears the flag**, mirroring `ApplyMeetingType`.
   **No unique index, no de-duplication on upgrade:** existing duplicate results are real user documents.
6. **Validators.** Save-time **scope check** (a Platform template may reference only Platform/Diariz enabled
   formulas) -> 400 naming the offending formula. **In-use check** on formula delete *and* disable -> 400 naming
   the templates.
7. **Converge the half-migrated scoping** flagged in the spec: `RecordingsController.ApplyMeetingType` and the
   minutes generator still filter meeting types by `UserId` while the controllers filter by `RoomId`. This
   phase touches both paths - fix them here rather than leaving a trap.

**Tests.** Integration (real Postgres) for the conversion migration: scopes preserved, `PrimaryFormulaId` set,
admin edits intact. Upsert + `IsUserEdited` skip. Both validators. Backup: a Format-1 archive is rejected.

**Release:** functional enhancement -> **minor**. **Bumps `CurrentFormat`.** Server redeploy. Schema + synopsis
docs.

---

## Phase 3 - Built-in content as files

**Goal:** one authoring format for all built-ins; retire the C# template DSL.

- Extend `BuiltInFormulaCatalog` to parse the body as a **template**: `#`/`##`/`###` -> a section at that level;
  content before any heading -> a **level-0** section; `{{field}}` -> a field block; `[[WRITE: …]]` -> a prompt
  block (the marker `SingleCallMinutesStrategy` already emits); `---` -> `hr`; anything else -> boilerplate.
  Emit **no** `breakAfter`, so the composer's legacy rule applies - which is what the seeded templates rely on
  today, so `Date: {{date}}` still renders on one line.
- Convert the 4 existing formula files to level-0 single-prompt files (identical output).
- Add **8 Diariz minutes formulas**, one per seeded template, carrying exactly today's content. The seeder links
  each seeded `MeetingType.PrimaryFormulaId` by `Key`.
- Delete `MeetingTypeSeeder`'s C# content DSL (`Std`/`Sec`/`Text`/`Field`/`Prompt`).

**Tests.** The parser (each block kind, headless preamble, malformed input skipped not fatal - the catalog
already tolerates a bad file). The golden test from Phase 1 now covers the file-authored versions.

**Release:** chore/refactor -> **build bump** (no user-visible change if the golden test holds). Server redeploy.

---

## Phase 4 - UI

- **Extract `TemplateContentEditor`** from `ManageMeetingTypesModal` (the section/block editor, its
  drag-and-drop, and the `meetingTypeDraft.ts` kernel) into a shared component. Already pure and well-tested -
  a move, not a rewrite.
- **`FormulaEditModal`**: the prompt `<textarea>` becomes `TemplateContentEditor`. Context checkboxes stay.
- **`ManageMeetingTypesModal`**: loses the content editor; gains a **primary formula** picker and an
  **additional formulas** multi-select, both filtered to what the template's scope may reference. Icon, colour,
  title, group, overview all stay.
- **`meetingTypeIo.ts`**: export references formulas by **name + scope** instead of embedding a prompt; import
  resolves them and reports any it cannot.

**Tests.** The shared editor; both modals; the pickers only offer referenceable formulas.

**Release:** functional enhancement -> **minor**. README Features + `docs/features.md` + About `CAPABILITIES`
(formulas are now structured; a meeting type drives a set of formulas). Server redeploy.

---

## Sequencing and risk

| Phase | Reversible? | Risk | Guard |
|---|---|---|---|
| 0 | yes | none | shipped |
| 1 | yes (additive migration) | a migrated formula's output drifts | the golden byte-identical test |
| 2 | **no** (drops columns) | conversion corrupts templates; re-run destroys an edited result | integration tests on real Postgres; `IsUserEdited` skip; `CurrentFormat` fence |
| 3 | yes | a hand-authored file parses wrong | parser tests + the golden test |
| 4 | yes | - | component tests |

**Phase 2 is the point of no return.** Take a backup before deploying it; the `CurrentFormat` bump means a
Format-1 archive can no longer be restored onto it.

## Verification (each phase)

- `dotnet test` (unit + Testcontainers integration) and `cd apps/web && npm test && npm run build`, both clean -
  no errors, no warnings.
- Drive the app: run a formula from the hub tile; `/formula <name>` in chat; apply a meeting type and confirm
  the minutes come from its primary formula and the additional formulas land in the Formulas tab; delete a
  referenced formula and get a clear 400.
- **Re-run semantics explicitly:** run the same formula twice -> still **one** result row, same id and position;
  hand-edit a result, re-transcribe -> the automatic run **left the edit intact**; run it manually -> it **did**
  replace it.
- **Backup fence:** restore a Format-1 archive after Phase 2 -> hard-rejected.
