# Recordings preferences panel redesign

Date: 2026-08-11
Source: `design_handoff_recordings_preferences` (Claude Design handoff, high fidelity)

## Problem

`apps/web/src/components/RecordingsSection.tsx` is a bare radio list plus two labelled number fields
with a Save button buried mid-panel. Nothing states the consequence of a placement choice; the
auto-stop durations look editable whether or not auto-stop is on (they are merely `disabled`, at
`opacity-50`); and the panel's Save sits in the body while the modal's own footer holds only Close.

## Scope

A visual and interaction redesign of one Preferences tab, plus the small shell mechanism the design
needs. **No server change, no schema change, no new persisted field.** The save payload is byte-for-byte
what it is today.

Out of scope, deliberately: the other five Preferences tabs keep their in-body Save; `MoveToSectionModal`
is untouched; `FolderPicker`'s internals are untouched.

## Decisions taken during design

Four questions the handoff left open were resolved with the repo owner. Each is recorded here with its
reasoning, because each rejects something the handoff asked for.

### 1. The user-facing word stays "Folder", not "Section"

The handoff proposes renaming the domain word to **Section** in the UI and sweeping the rest of the app
to match. Measurement showed the premise is inverted: **the app already says "Folder" to users
everywhere** - the nav, the help articles, the `workspace` catalogue, and the published n8n node, whose
`enhancements.ts` deliberately maps the wire field `sectionId` to the display name "Folder ID".
"Section" appears only in code, the database, and the REST surface.

So a Section↔Folder translation layer already exists on purpose. The redesign adopts every visual and
structural decision in the handoff and changes only the noun.

The reverse - renaming the *code* to Folder so it matches the UI - was assessed and rejected as too
expensive for a cosmetic gain:

| Cost | Detail |
| :--- | :--- |
| Identifiers | ~4,900 occurrences (Domain 2,194, tests 1,022, web 1,008, API 592, n8n 72, worker 46) |
| Database | The `Sections` table, its FKs and indexes, referenced by all 80 migrations |
| Backups | A table rename is a semantic reshape an older dump cannot survive, forcing a `MaintenanceController.CurrentFormat` bump, which **hard-rejects every backup currently held** |
| Public API | `/api/sections`, `/api/sections/{sectionId}/folder-attachments`, `/api/sections/{sectionId}/formulas/{formulaId}/run`, `/api/recordings/{id}/section`, `/api/recordings/section`, plus `sectionId` on many DTOs - consumed by the published npm n8n node, Zapier, and MCP OAuth clients |

### 2. Footer Save is opt-in, not a shell-wide refactor

The design moves Save into the modal footer. `PreferencesModal` is shared by six tabs and every one of
them self-saves in its own body; four of them (Calendars, Integrations, Assistant, Formulas) have
several independent save actions rather than one form, so migrating all six is a large refactor with
real regression risk well outside this redesign.

Instead the modal gains a small **registration context**. Recordings registers; the other five register
nothing and see today's footer unchanged. The mechanism is there if other tabs migrate later.

### 3. The picker keeps today's row semantics

The handoff's picker inverts `FolderPicker`'s rule: there, a row body **drills** and a separate button
**chooses**; the handoff has the body **choose** and a chevron **drill**.

The stated motive is a real inconsistency: today a row body drills at a drill level but chooses at the
root ("Ungrouped") and for every filtered match - so the same click means different things depending on
whether the filter box has text in it. That inconsistency is signposted, though, not unmarked: a drill
row carries a folder glyph, a `>` chevron and a separate select button; a choose-only row carries none
of them. It is a wart in the rule, not in the appearance.

Inverting it would make selecting the large easy target and drilling a small chevron. On a tree that can
nest 8 levels deep, browsing is the frequent action and selecting happens once at the end, so that is the
wrong way round; and the body-drills gesture is already learned from the left nav. Rejected.

The new picker is therefore **modal chrome around the existing `FolderPicker`, rendered unchanged**.

### 4. One PR

Shipped as a single release.

## Design

### A. Copy

Every string goes through i18n; no hardcoded English. `apps/web/src/locales.test.ts` enforces exact key
parity and non-empty values across en/de/es/fr, so all four catalogues change together with real
translations.

New and changed keys in the `account` namespace:

| Key | English value | Status |
| :--- | :--- | :--- |
| `placementHeading` | Where a new recording is filed | new |
| `placementHeadingMeta` | in your personal space | new |
| `placementDefaultChip` | Default | new |
| `placementSelected` | The folder I'm looking at | changed |
| `placementSelectedMeta` | Files into whichever folder is open in the list when you start recording. | new |
| `placementUngrouped` | Always Ungrouped | changed |
| `placementUngroupedMeta` | Everything lands in one place; file it into a folder afterwards. | new |
| `placementSpecific` | One fixed folder | changed |
| `placementSpecificMeta` | Always the same folder, wherever you happen to be. | new |
| `placementChange` | Change... | new |
| `calendarAutoStopHeading` | Let a calendar meeting end its own recording | new |
| `calendarAutoStopBody` | Only when you join the meeting from your calendar, so its end time is known. A recording you start with the Record button is not cut short by this option. | new |
| `calendarStopPrefix` | Stop | new |
| `calendarStopMinutesSuffix` | minutes after the meeting was due to finish, | new |
| `calendarSilencePrefix` | or after | new |
| `calendarSilenceSuffix` | seconds of silence - whichever comes first. | new |
| `calendarAutoStopExample` | A 10:00-11:00 meeting keeps recording until {{until}} - or stops sooner, once {{seconds}} seconds pass with nobody speaking. | new |
| `unsavedChanges` | Unsaved changes | new |

Retired from all four catalogues: `recordingsIntro`, `calendarRecordingHeading`,
`calendarRecordingIntro`, `calendarAutoStop`, `calendarAfterMinutesHint`, `calendarSilenceSecondsHint`.

`calendarAfterMinutes` and `calendarSilenceSeconds` are kept, no longer as visible labels but as the two
number inputs' `aria-label`s - the sentence fragments around a field are not a label.
`placementFolder` is retired as a visible label; the picker dialog names itself.

New keys in the `workspace` namespace, beside the existing `folderPicker*` family:

| Key | English value |
| :--- | :--- |
| `folderPickerTitle` | Choose a folder |
| `folderPickerSubtitle` | Every new recording will be filed here. |
| `folderPickerChosen` | Chosen: {{path}} |
| `folderPickerDone` | Done |
| `folderPickerCloseAria` | Close folder picker |

No em or en dashes anywhere in this copy - plain hyphens only.

### B. Footer-save registration (`PreferencesModal`)

A context defined alongside the modal. A tab opts in with a hook:

```
usePreferencesFooter({ dirty, busy, status, onSave })
```

- The modal holds the registered value in state and renders **Save changes** only while a handler is
  registered. Unregistered tabs get today's footer (Close alone).
- The footer becomes `space-between`: left is the status line, right is Close then Save changes.
- `status` is one of `"idle" | "unsaved" | "saved"` plus an optional error string. `"unsaved"` and
  `"saved"` render as muted 13px text; an error renders in red, keeping the existing
  `apiErrorMessage(e)` treatment.
- **Identity churn:** only primitives (`dirty`, `busy`, `status`) go into the effect's dependency list.
  The `onSave` callback is held in a ref that is updated every render, so a new closure each render
  cannot loop the registration effect.
- Cleanup deregisters on unmount, which is what makes switching tabs restore the plain footer.

The header gains a breadcrumb: **Preferences** (16px/600) then **/ {active tab label}** (13px, muted).
It is derived from the existing `tabs` array, so it is correct on all six tabs rather than hardcoded.

### C. The panel (`RecordingsSection`)

State, seeding from the `["user-settings"]` query, and the `if (!data) return null` guard are unchanged.
Two additions: `pickerOpen: boolean`, and a derived `dirty` computed by comparing local state against the
seeded values, so undoing an edit by hand clears the indicator rather than latching it.

**Group A - "Where a new recording is filed"**, with "in your personal space" as muted meta on the same
baseline. Three cards, each a `<label>` wrapping a native `<input type="radio">` in one `name` group, so
arrow-key navigation still works. Each card carries a title and a one-line consequence. The
`SelectedFolder` card carries a `DEFAULT` chip.

**Selection is drawn with the card's own `border-color` and `background`.** Never an outset ring: the
content pane scrolls, and a ring painted 1px outside the box makes the pane wider than its client width,
which paints a full-width horizontal scrollbar across the design. `overflow-x: hidden` stays on the pane
as a second line of defence.

Choosing `SpecificFolder` reveals a row indented to align under the card title:

- a **path chip** - folder glyph plus the folder's full path, taken from `orderedSections` so there is
  one source of truth for path labels. That means the separator is the guillemet `orderedSections`
  already emits, not the ` > ` the handoff's mockup draws - a deliberate, minor deviation in favour of
  matching the path strings shown elsewhere in the app. `null` renders as "Ungrouped".
- a **Change...** button that opens the picker.

Leaving `SpecificFolder` hides the row but keeps the remembered id in local state; the save payload still
sends `placementSectionId: null` for any other mode.

**Group B - "Let a calendar meeting end its own recording"**, one bordered card:

- Header row: calendar glyph, title, body copy, and a `<button role="switch" aria-checked>` on the right
  whose accessible name is the title.
- When on, a revealed body with a top border and faint fill holds two sentence rows -
  `Stop [n] minutes after the meeting was due to finish,` / `or after [n] seconds of silence - whichever
  comes first.` - and a worked example with a tinted left rule, recomputed on every keystroke and marked
  `aria-live="polite"` so a value change is announced.
- The rows wrap rather than squash at the modal's `min-w-[40rem]`.
- The two fields are **absent** when auto-stop is off, not disabled. `disabled:opacity-50` disappears.

`positiveOr(value, fallback)` is unchanged: a blank or non-positive duration still falls back to 3 / 30
on save. The Save payload is exactly today's five fields, followed by
`qc.invalidateQueries({ queryKey: ["user-settings"] })`.

The in-body Save button is removed; the panel registers with the footer instead.

### D. `FolderPickerModal` (new)

A centred `role="dialog" aria-modal="true"` over the Preferences dialog: backdrop, 420px wide, popover
surface/border/shadow from the existing `--hub-popover-*` tokens.

- **Header:** title, subtitle, and a close X.
- **Body:** `<FolderPicker sections selectedId onSelect />`, rendered unchanged.
- **Footer:** `Chosen: <full path>` on the left, truncating, and a primary **Done** on the right that
  only closes - the choice is already applied to local state, and is still persisted only by the panel's
  Save.
- **Focus:** moves to the filter box on open, returns to `Change...` on close.
- **Escape:** the picker's own handler calls `stopPropagation()` on the native event. `PreferencesModal`
  listens on `document`, and React delegates from the root container, so stopping propagation there does
  prevent the document listener - Escape closes the picker and leaves Preferences open. When the filter
  box is non-empty `FolderPicker` already swallows Escape to clear the filter, so that case clears the
  filter and closes nothing.

**No new icons.** The handoff asks for Feather `check` and `x` to be added to `icons.tsx`. Neither is
needed: the tick stays inside `FolderPicker`, which is unchanged, and every other dialog in this codebase
renders its close control as a literal `✕` character with an `aria-label` (`ManageUsersModal.tsx:65`,
`ManageMeetingTypesModal.tsx:208`, `ChatPanel.tsx`, and others). Introducing an icon component for this
one dialog would read as inconsistent with everything around it.

### E. Known deviations from the handoff

1. **"Section" is "Folder"** throughout - see decision 1.
2. **The picker's list body keeps today's semantics and styling.** The screenshots show a restyled filter
   box (search glyph, borderless input) and inverted row rule; both live inside `FolderPicker`, which
   `MoveToSectionModal` also renders. This PR supplies the new chrome only. Follow-up if wanted.
3. **No new icon components** - the close control follows the codebase's existing `✕` convention, as
   above.
4. **The path chip's separator** is `orderedSections`' guillemet, not the handoff's ` > `.

## Testing

Repo TDD applies: failing test first, watch it fail, then the minimal code.

**`RecordingsSection.test.tsx`** - largely rewritten, but **every existing save-payload assertion is kept
verbatim**, since the payload is the one thing that must not change:

- the three placement modes, defaulting to `SelectedFolder`;
- specific-folder save with a folder chosen through the picker, and nothing else in the body;
- Ungrouped chosen via the picker root;
- `placementSectionId` cleared when a non-specific mode is chosen;
- the saved folder marked current on round-trip;
- the auto-stop trio round-tripping, and blank/zero falling back to 3 / 30.

Changed and new:

- "disables both conditions until auto-stop is on" becomes **"mounts the two fields only when the switch
  is on"** - they are absent now, so a `disabled` assertion would be testing something that no longer
  exists;
- the example sentence recomputes from the two values (assert the rendered text, both defaults and after
  a change, so a hardcoded string fails);
- `dirty` clears when an edit is reverted by hand;
- the keyboard-order test moves into the picker modal's test.

**`PreferencesModal.test.tsx`** - Save appears only while a tab registers a handler; it disappears when
switching to a tab that does not; the status line reflects idle/unsaved/saved; an error renders in red;
the breadcrumb names the active tab.

**`FolderPickerModal.test.tsx`** (new) - opens with focus on the filter box; Escape closes the picker and
**not** Preferences (assert an `onClose` spy on the outer modal was not called); Escape with a non-empty
filter clears the filter and closes nothing; Done closes; `Chosen:` renders the full path; focus returns
to `Change...`.

**Not tested in jsdom:** the `overflow-x` invariant. jsdom computes no geometry, so a class-presence
assertion there would prove nothing. Verified in the browser preview instead, and reported as such.

## Release checklist

Functional enhancement, so **minor +1**: `0.203.0` -> `0.204.0`.

1. `version.json` plus its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`, with the real PR number.
3. `CAPABILITIES` - review; this is a redesign of an existing capability, so likely no row change.
4. README Features row + `docs/features.md` bullet - update **in lockstep** if either describes the
   placement preference or where its Save lives.
5. `content/help/en/**` - the behaviour a user relies on changes (Save moves to the footer; the folder
   chooser becomes a dialog), so the affected articles are updated, and their translations with them.
6. `docs/Overall_Synopsis_of_Platform.md` and `docs/Data_Schema.md` - **no edit**; no architecture,
   contract, endpoint or schema change. Say so explicitly in the PR.

**Deployment surface:** web only. Server redeploy, **no desktop release** - nothing under
`apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js` or desktop dependencies is
touched. The lockstep bump to `apps/desktop/package.json` does not by itself require one.
