# Formulas - polish + file-based built-ins + Shared Formulas (design)

> Follow-up to the original Formulas feature (`2026-07-12-formulas-design.md`, shipped v0.130.0). This
> covers the post-testing tweaks and two new subsystems. Delivered as **four independent PRs** (Phases A-D),
> each producing working, testable software on its own. Each phase is its own branch -> PR -> CI (main is
> branch-protected), one release per PR, docs in lockstep.

## Context

The Formulas feature shipped and works: a saved **Formula** (Prompt + Context) is **Run** over a recording to
produce a **Result** (a named Markdown document), from a transcript tab, `/formula` in chat, or the MCP
`run_formula` tool. After hands-on testing the owner wants: modal/branding polish; the built-in Diariz
formulas moved out of C# into git-editable files; a two-panel Formulas tab; and a **Shared Formulas** system
so users can publish a Personal formula and others can find, view, and subscribe to it (a live link, not a
copy).

This spec assumes the current implementation exactly as mapped on 2026-07-13 (entities `Formula`,
`FormulaResult`, `FormulaScope{Personal=0,Platform=1,Diariz=2}`, `[Flags] FormulaContext`; controllers
`FormulasController`/`FormulaResultsController`; `FormulaRunner`; web modals `FormulaEditModal`,
`FormulaRunModal`, `FormulasManager`, `FormulasToolbar`, `ManageFormulasModal`, `FormulasSection`,
`FormulaResultEditModal`, `FlaskIcon`; `useResizableWidth`; `Avatar`; logo at `apps/web/public/logo.png`).

## Locked decisions (from brainstorming, 2026-07-13)

1. **Built-in format:** markdown files with a small `key: value` frontmatter, one file per formula.
2. **Edit propagation:** seeding stays **create-only by name** - new files added on deploy; existing
   formulas (by name) never overwritten (admin edits + old copies preserved).
3. **Platform-formula icon:** Diariz logo for both Diariz **and** Platform formulas (Platform records no
   author). No new author column.
4. **Unsubscribe:** users can remove a shared formula they added; owner delete still cascades everyone.

Confirmed by the owner: sharing is **platform-wide** (any user sees a shared formula's prompt/description/
owner name+avatar in discovery); owner edits **propagate live** to subscribers (link, not copy); disabling
sharing / deleting the formula removes it for subscribers.

---

## Phase A - Modal & icon polish (web only, no schema)

**Release:** Build bump (refinement). **Deployment:** server redeploy (web).

- **`FlaskIcon.tsx`**: add a **liquid fill in a bright-blue gradient** inside the flask. The icon is used at
  `currentColor` for strokes across the app, so the gradient must apply **only** to a fill-only "liquid"
  sub-path with a **unique gradient id** (avoid collisions when multiple icons render on one page - derive a
  stable per-instance id or use a fixed id that is safe because the `<defs>` is identical). Strokes stay
  `currentColor`. Verify the icon still reads correctly at toolbar size and in the empty-state large size.
- **`FormulaEditModal.tsx`** (drives New + Edit, Personal + Platform):
  - **Do not close on backdrop click** (remove the backdrop `onClick={onClose}`); Escape + explicit
    Cancel/Close still close. (Matches `ManageFormulasModal`.)
  - **Flask icon left of the title** (`New Formula` / `New Platform Formula` / `Edit Formula`).
  - **Widen** the dialog (`max-w-2xl` -> `max-w-4xl`) so the prompt wraps less.
  - **Context checkboxes collapse to a single `flex-wrap` row** (from the 2-col grid).
  - **Taller prompt textarea** using the reclaimed vertical space.
- **`FormulaRunModal.tsx`**: flask icon left of "Run a formula" (no behaviour change).
- **`ManageFormulasModal.tsx`** -> rename to **"Manage Platform Formulas"** (modal title + the `UserMenu`
  account-menu item `account.manageFormulas`), flask icon left of title, move **New formula** to the footer
  **bottom-left**, shrink **Close** and move it **bottom-right**.
- **About-box `CAPABILITIES` enrichment** (the deferred docs tweak): update the Formulas row and the
  "Connect Claude (MCP)" row in `apps/web/src/lib/releases.ts` to match the README (three scopes, `/formula`
  in chat, Claude via MCP `run_formula`, admin management). No em/en dashes.

## Phase B - File-based built-in formulas (API only)

**Release:** Build bump (refactor + small enhancement). **Deployment:** server redeploy (API).

- New folder `src/Diariz.Api/formulas/` with one `*.md` file per built-in, **markdown + frontmatter**:
  ```
  ---
  name: Follow-up email
  description: Draft a concise follow-up email after the meeting.
  context: Transcript, Summary, Actions
  ---
  <the prompt markdown body, verbatim>
  ```
  `context` is a comma-separated list of `FormulaContext` flag names (`Transcript, Notes, Attachments,
  Summary, Minutes, Actions`).
- A **pure** loader (unit-testable, no I/O in the parse function): split on the first `---`-delimited
  frontmatter block, parse `key: value` lines, map `context` names -> `FormulaContext` bitmask, take the
  remaining body as `Prompt`. **No new NuGet dependency.** A thin file-reading wrapper enumerates the folder.
- The four existing C# literals (`Follow-up email`, `Meeting recap`, `Decisions & risks`, `Tone & sentiment
  read`) are moved verbatim into files - behaviour-preserving.
- `Seeder.SeedFormulasAsync` reads the folder instead of the literals; **still create-only by name**. Files
  are marked `CopyToOutputDirectory=PreserveNewest` so they ship in the published API / Docker image; the
  loader resolves the folder relative to the app content root.

## Phase C - Two-panel Formulas tab (web + small API DTO add)

**Release:** Minor bump (functional enhancement). **Deployment:** server redeploy (web + API).

- The Formulas tab body becomes a **resizable horizontal split** (reuse `useResizableWidth`, persisted to a
  new localStorage key): **left** = the runs list (`FormulasManager`), **right** = the selected result
  rendered read-only via `renderMarkdown`. The existing top toolbar is unchanged and still acts on the
  selection. Empty state (no runs) shows the current centered empty message across the whole body.
- Each left-list row gains an **origin icon**:
  - **Diariz** and **Platform** formulas -> the Diariz logo (`/logo.png`).
  - **Personal** (mine) -> the running user's avatar (`Avatar`).
  - **Shared** (once Phase D lands) -> the sharer's avatar.
- To drive the icon, `FormulaResultDto` gains an `origin` object:
  `{ kind: "diariz"|"platform"|"personal"|"shared", displayName: string, initials: string, pictureUrl: string? }`
  resolved server-side from the linked formula's scope/owner. **Graceful fallback** when `FormulaId` is null
  (formula later deleted -> `SET NULL`): `kind` derived from a stored hint or defaulted to `personal`/unknown
  with the result's `CreatedBy` display. (Since a deleted formula loses scope, use the result's `CreatedBy`
  as the person and a neutral kind.)

## Phase D - Shared Formulas (API + web; new schema)

**Release:** Minor bump (functional enhancement). **Deployment:** server redeploy (API + web).

### Schema (one additive migration; forward-restore-safe -> no `MaintenanceController.CurrentFormat` bump)
- `Formula.Shared` (bool, default false) - only meaningful for Personal scope.
- New table **`FormulaSubscription`**:
  - `Id` (guid, PK)
  - `FormulaId` (guid, FK -> `Formula`, **CASCADE** on delete)
  - `UserId` (guid, FK -> `ApplicationUser`, **CASCADE** on delete)
  - `CreatedAt` (timestamptz)
  - **Unique index** `(FormulaId, UserId)`.
  - Deleting the formula cascades every subscriber's link (the required behaviour); deleting a user cascades
    their own links.

### Behaviour
- **Owner side:** a **"Share this Formula"** checkbox in the Personal formula editor (Preferences ->
  Formulas, `FormulaEditModal` at Personal scope) sets `Shared`. Only Personal formulas can be shared
  (Platform/Diariz are already universal - the checkbox is Personal-only).
- **Discovery:** on the Run picker, a **"Find shared formulas"** button next to "Manage formulas" opens a
  browser listing every `Shared` formula owned by **someone else**: sharer **avatar + name**, formula **name
  + description**, a **read-only view** of the prompt + context, and **Add to my formulas** / **Remove** (if
  already subscribed).
- **Run picker "Shared Formulas" group:** the formulas the caller has subscribed to. A subscribed formula is
  `scope=Personal, OwnerUserId != me`, so the grouping is derived client-side.
- **Access changes:**
  - `FormulasController.List` visibility now = `myPersonal + enabled Platform/Diariz + formulas I'm
    subscribed to`.
  - `FormulaRunner.EnsureCanRun`: a Personal formula runs if `OwnerUserId == me` **OR** (`Shared` && a
    subscription `(formula, me)` exists); otherwise `FormulaNotFoundException` (unchanged hide-existence
    behaviour). Running still resolves **the caller's** LLM config (unchanged).
  - Owners can un-share (`Shared=false`) or delete; both remove the formula for subscribers (un-share hides
    it from `List`/discovery; delete cascades the subscription rows).

### API (new endpoints on `FormulasController`)
- `GET /api/formulas/shared` - discovery list: all `Shared && OwnerUserId != me`, each with owner display
  (name/initials/pictureUrl), prompt, context, description, and `AlreadyAdded` bool.
- `POST /api/formulas/{id}/subscribe` - add to my collection (404 if not `Shared` or is mine; idempotent on
  the unique index).
- `DELETE /api/formulas/{id}/subscribe` - remove my subscription.
- `Formula.Shared` is settable via `CreateFormulaRequest`/`UpdateFormulaRequest` (Personal only; ignored/400
  for non-Personal) and surfaced on `FormulaDto`.

---

## Cross-cutting

- **Versioning / docs (CLAUDE.md release checklist), one release per PR:**
  - A: Build bump; `releases.ts` entry + the `CAPABILITIES` enrichment. Web redeploy.
  - B: Build bump; `releases.ts` entry. API redeploy. (Internal refactor - no user-facing feature row change,
    but note the built-ins are now file-driven in the synopsis if relevant.)
  - C: Minor bump; `releases.ts` entry; README/`features.md`/`CAPABILITIES` note the two-panel tab; `Data_
    Schema.md` unchanged (DTO-only), synopsis notes the origin field if relevant. API + web redeploy.
  - D: Minor bump; `releases.ts` entry; README Features row + `docs/features.md` bullet + `CAPABILITIES` row
    gain Shared Formulas; `Data_Schema.md` gains `FormulaSubscription` + `Formula.Shared` (+ migration
    history row); `Overall_Synopsis_of_Platform.md` gains the sharing model + endpoints. API + web redeploy.
  - No phase touches the desktop shell -> **no desktop release**.
- **TDD throughout.** Integration tests (real Postgres) for: the `FormulaSubscription` cascades (formula
  delete and user delete), the extended run-access rule, and the extended `List` visibility - the in-memory
  provider cannot verify FK cascade. Unit tests for the frontmatter loader (pure), the origin resolver, and
  the controllers' access branches. Vitest for the split-panel selection/render and the discovery browser.
- **Conventions:** no em/en dashes in user-facing copy; i18n keys added in lockstep across `en/de/es/fr`
  (`workspace`, `account`, `admin` namespaces as mapped); `FormulaContext`/`FormulaScope` remain append-only
  int enums; keep the `origin`/`Shared` DTO fields typed to avoid the `[Flags]`-as-string gotcha (Context
  stays `int`).

## Verification (of the eventual feature)
- **Phase A**: modals no longer close on outside click where specified; flask icon+gradient renders; Manage
  Platform Formulas layout (New bottom-left, Close bottom-right); About box shows enriched copy.
- **Phase B**: fresh DB seeds the 4 built-ins from files; editing a file's prompt affects only fresh installs;
  adding a new `.md` file adds a formula on next boot; unit tests cover the parser.
- **Phase C**: selecting a run renders its markdown on the right; left rows show correct origin icons;
  resizable width persists.
- **Phase D**: share a Personal formula -> another user finds it in discovery (sees prompt read-only + owner
  avatar) -> adds it -> it appears under "Shared Formulas" -> runs it (uses their own LLM config) -> owner
  edits propagate -> owner delete removes it from the subscriber; subscriber can also remove it themselves.
