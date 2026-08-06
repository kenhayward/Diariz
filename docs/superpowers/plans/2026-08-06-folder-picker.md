# Folder Picker Implementation Plan (Phase 5)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two flat folder pickers with one that you can type to filter or drill to browse, now that folders nest up to 8 levels.

**Architecture:** One new presentational component, `FolderPicker`, consumed by both existing pickers. It renders the same list two ways: an empty filter drills one level at a time; a non-empty filter shows matches from the whole tree with their paths.

**Tech Stack:** React 19 + TypeScript, Vitest + @testing-library/react, i18next.

## Why this exists, and what it is NOT

Both pickers render every folder as a flat list of `Parent > Child > Grandchild` strings. At 8 levels that is a long list of long strings, and the useful part of each label is at the end where it is most likely truncated.

**This is a comfort improvement, not a correctness fix.** Reachability was already fixed in Phase 2, when a whole-branch review found `orderedSections` silently omitted anything below depth 2 - so every folder is already selectable today. Nothing here unblocks a user; it makes an existing capability pleasant. Scope accordingly: if a change here starts looking risky, it is not worth it.

## Decision taken (2026-08-06)

**Type to filter, drill to browse.** An empty filter shows one level at a time, mirroring the nav. Typing matches across the whole tree, each hit shown with its path. Both modes render the same list.

## Two constraints found while scoping

1. **`RecordingsSection` uses a native `<select>`.** A native `<option>` cannot hold interactive UI, so that consumer is a genuine component swap, not a different list. Expect it to be the larger of the two wiring tasks.
2. **A picker row must distinguish "choose this" from "go into this".** The nav already solved this exact problem - the row body drills, a separate target opens the page. Follow that split rather than inventing a second convention for the same gesture.

## Global Constraints

- **TDD is mandatory.** Write the failing test, RUN it, watch it fail, then write the minimal code.
- **Test output must be pristine.**
- **No em dashes or en dashes in user-facing text.** Plain ASCII hyphen.
- **Any new or changed i18n string goes in all four catalogues** (`en`, `de`, `es`, `fr`), genuinely translated, accents preserved. Four separate tasks across this project shipped English-only strings.
- **`main` is branch-protected.** This lands as one PR.
- **One release:** `0.181.0` -> **`0.182.0`** (functional enhancement) across `version.json` and its four mirrors, plus a `RELEASES[0]` entry matching.
- **No EF Core migration**, no server change. This phase is web-only.
- **Deployment surface: server redeploy only.**

## File Structure

**Created:**

| File | Responsibility |
| --- | --- |
| `apps/web/src/components/FolderPicker.tsx` | The picker: filter box, drill list, flat match list, selection. |
| `apps/web/src/components/FolderPicker.test.tsx` | Its tests. |

**Modified:**

| File | Change |
| --- | --- |
| `apps/web/src/components/MoveToSectionModal.tsx` | Flat button list -> `FolderPicker` |
| `apps/web/src/components/RecordingsSection.tsx` | Native `<select>` -> `FolderPicker` |
| `apps/web/src/locales/{en,de,es,fr}/workspace.json` | New strings |

---

### Task 1: The `FolderPicker` component

**Files:**
- Create: `apps/web/src/components/FolderPicker.tsx`, `apps/web/src/components/FolderPicker.test.tsx`
- Modify: the four `workspace.json` catalogues

**Interfaces:**
- Consumes: `SectionDto`; `orderedSections` from `apps/web/src/lib/sectionTree.ts`, which already returns `{ section, label, depth }` for every folder at any depth in display order - it was given `depth` in Phase 2 for exactly this. `breadcrumbOf` from `drillView.ts` for the drill header.
- Produces: `<FolderPicker sections={...} selectedId={...} onSelect={(id: string | null) => ...} />`. `null` means the root, which the consumers label "Ungrouped".

**Behaviour:**
- A filter box at the top.
- **Empty filter:** the folders at the current drill position, plus a way back up. Root is offered as a selectable row.
- **Non-empty filter:** every folder whose name matches, from the whole tree, each with its path. No drilling in this mode - the point is to skip it.
- **Choosing versus entering** are distinct targets on a row, following the nav's split. Whichever you pick, be consistent and say so in a doc comment.
- The currently-selected folder is marked.
- The drill position is component state, not the URL - this is a picker, not the nav.

- [ ] **Step 1:** Write the failing tests: filtering shows deep matches with their paths; clearing the filter returns to drilling; drilling into a folder shows its children; choosing a folder calls `onSelect` with its id; choosing the root calls it with `null`; the selected folder is marked.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Add the strings to all four catalogues, then implement.
- [ ] **Step 4:** Run focused, then the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 2: Wire `MoveToSectionModal`

**Files:**
- Modify: `apps/web/src/components/MoveToSectionModal.tsx` and its tests

**Context:** This is the discoverable path to moving a recording, reached from a recording's kebab. Today it renders `orderedSections` as a flat list of buttons, marks the current folder with a tick, and has a "create a folder and move into it" form below.

Replace the list with `FolderPicker`. **Keep the create-and-move form** - it is a separate affordance and nothing here justifies removing it. Note it currently creates at the top level (`parentId: null`); leave that as-is unless a test proves otherwise, and say in your report whether you think it should follow the picker's drill position instead.

The modal's real label is **"Move to section"** (`moveToSectionTitle`), not "Move to folder" - a previous help edit in this project invented the latter. Do not rename anything.

- [ ] **Step 1:** Write/adjust the failing tests: the modal renders the picker; choosing a folder calls `api.moveRecording` with that id; choosing the root ungroups.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run focused, then the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 3: Wire `RecordingsSection`

**Files:**
- Modify: `apps/web/src/components/RecordingsSection.tsx` and its tests

**Context:** The placement chooser - where a new recording gets filed. It uses a native `<select>` with an `<option>` per folder plus an "Ungrouped" option, shown only when the placement mode is `SpecificFolder`.

A native `<option>` cannot hold interactive UI, so this is a real swap rather than a list change. Replace the `<select>` with `FolderPicker`, keeping the surrounding radio-group behaviour and the label intact. The chooser lives in a settings pane, so an inline picker is fine - it does not need a popover.

Watch the accessibility: the `<select>` currently carries `aria-label={t("placementFolder")}` and the visible label above it. Whatever replaces it must remain labelled and keyboard-reachable - a `<select>` is keyboard-navigable for free and a custom list is not.

- [ ] **Step 1:** Write/adjust the failing tests: the picker renders when the mode is `SpecificFolder`; choosing a folder sets the placement; the Ungrouped choice sets null.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run focused, then the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 4: Release chores

**Files:**
- Modify: `version.json` + four mirrors, `apps/web/src/lib/releases.ts`, `README.md`, `docs/features.md`, `apps/web/src/content/help/en/organizing-folders.md`

- [ ] **Step 1:** Bump to `0.182.0` in all five places.
- [ ] **Step 2:** Add the `RELEASES[0]` entry. The controller supplies the PR number. Check the date against the entry below it - `releases.test.ts` asserts non-increasing dates, and a stale date has broken this three times in this project.
- [ ] **Step 3:** Update the About-box `CAPABILITIES` row, the README Features row and the `docs/features.md` bullet **in lockstep**, only where the picker actually changes what is described.
- [ ] **Step 4:** Update `organizing-folders.md` where the behaviour a user relies on has changed. **ASCII only**, front matter intact. Name controls by their **actual** i18n strings.
- [ ] **Step 5:** Full verification: web suite + build. No backend change this phase, but run `dotnet build Diariz.slnx` once to be sure nothing drifted. Commit. **Do not push or open a PR.**
