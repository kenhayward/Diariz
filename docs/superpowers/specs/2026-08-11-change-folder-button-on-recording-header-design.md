# Change Folder button on the recording header

**Date:** 2026-08-11
**Status:** Approved, ready for planning

## Problem

A recording's folder placement is shown on the detail page as a row of navigable chips
(`FolderChips`, under the recording name). Changing that placement is possible today, but only
through the kebab menu's "Move to section" item - there is no visible control next to the
breadcrumbs that says the placement can be changed. The affordance to *read* where a recording is
filed sits right next to no affordance to *change* it.

Two defects sit behind that menu item, both reached by this work:

1. **The breadcrumb goes stale after a move.** `MoveToSectionModal.move()` and `createAndMove()`
   invalidate only the `["recordings"]` list query. The chips are derived from `rec.rooms`, which
   comes from the detail query `["recording", id]`. Those keys do not prefix-match, so after moving
   a recording from its own detail page the chips keep showing the old folder until a reload.
2. **The picker does not mark the current folder.** The `RecordingDetail` call site omits
   `currentSectionId`, so the modal falls back to its `UNKNOWN_SECTION` sentinel and highlights
   nothing. The left-nav call site (`useRecordingActions`) does pass it. For a control named
   "Change Folder", showing what the folder currently *is* is part of the job.

## Goals

- A visible "Change Folder" button on the recording detail header, immediately left of the folder
  breadcrumbs, opening the existing folder picker.
- The breadcrumbs repaint as soon as the move lands, with no reload.
- The picker opens with the recording's current folder marked.

## Non-goals

- No new picker component. The existing `MoveToSectionModal` (and the `FolderPicker` inside it) is
  reused unchanged in behaviour.
- No change to how a move is applied. Clicking a folder still moves immediately and closes the
  dialog; there is no separate Save step to add.
- No change to the folder/section vocabulary split (users read "Folder", the code and API say
  "Section"). This is deliberate and settled.
- No bulk move, no drag-to-move, no move across rooms.

## Design

### 1. The button

The button is rendered by `RecordingDetail`, **not** inside `FolderChips`.

`FolderChips` renders a `<nav aria-label="Folder this recording is in">` - a navigation landmark
whose contents are all links to places. "Change Folder" is an action, not a destination, so placing
it inside that landmark would misdescribe it to assistive technology. `FolderChips` therefore keeps
its markup and its purely presentational, navigation-only contract.

The existing render guard gains a flex wrapper:

```jsx
{folderPlacement && (
  <div className="-mt-1 flex flex-wrap items-center gap-2">
    <button type="button" onClick={() => setMoving(true)} >
      {t("workspace:changeFolder")}
    </button>
    <FolderChips roomName={currentRoom?.name ?? ""} crumbs={folderCrumbs} onSelect={openFolderInList} />
  </div>
)}
```

The button reuses the `moving` state that already drives the kebab item, so it opens the same modal
through the same path. No new state, no second modal instance.

**Appearance:** a small bordered button carrying the visible text label, sized to sit level with the
chips (`text-xs`, matching vertical padding). It is deliberately **not** `ToolbarButton`, which is
icon-only with the label supplied through `title`/`aria-label` - this control needs its text
visible, since an unlabelled glyph next to a row of folder chips reads as one more chip.

It is **text-only, with no icon**. `FolderChips` already opens with an `aria-hidden` folder glyph
that exists to say "these are folders"; a folder icon on the button immediately to its left would
put two folder glyphs side by side and blur which one belongs to which control. Its square corners
(`rounded-md`) against the chips' pills (`rounded-full`) are what separate action from navigation
visually, and that separation does not need an icon to carry it.

**Placement decision:** the button lives *inside* the existing `folderPlacement` guard, so it
appears and disappears together with the chips. A recording with no placement in the room being
viewed shows neither. Changing folder in that case remains available through the kebab menu.

**The `-mt-1` moves up.** `FolderChips`'s nav carries `-mt-1` to counteract the hero's
`space-y-2.5` and keep the path tight under the recording name. The nav is no longer the outermost
element of that block, so the margin moves to the new wrapper and the class is dropped from the
nav. `FolderChips`'s doc comment explains that margin and is updated with it. `FolderChips` has
exactly one consumer, so no other caller is affected.

### 2. Breadcrumb refresh

In `MoveToSectionModal`, both `move()` and `createAndMove()` add the detail query to what they
invalidate, alongside the existing list invalidation:

```js
qc.invalidateQueries({ queryKey: ["recording", recordingId] });
```

The modal already receives `recordingId`, so this needs no new prop.

This is a fix to a component with two call sites. The left-nav site
(`useRecordingActions`) also gets it: harmless there, and correct - if a recording's detail page is
open while it is moved from the nav, that page was equally stale before.

### 3. Marking the current folder

The `RecordingDetail` call site passes the placement it already computed:

```jsx
<MoveToSectionModal
  recordingId={id}
  currentSectionId={folderPlacement?.sectionId ?? null}
  roomId={currentRoom && !currentRoom.isPersonal ? currentRoom.id : undefined}
  onClose={() => setMoving(false)}
/>
```

`folderPlacement` is computed earlier in the same render (after the `if (!rec) return` guard), so it
is in scope at the modal's render site.

Note the distinction the modal's own `UNKNOWN_SECTION` comment draws: `null` means "filed at the
room's top level" and is a real, markable value; `undefined` means "caller does not know". Passing
`?? null` is correct here precisely because a recording with a `folderPlacement` and no `sectionId`
genuinely is at the top level.

## Testing

Test-first throughout, per the project's TDD rule. Each test must be observed failing for the right
reason before the implementation is written.

**`MoveToSectionModal.test.tsx`** - the bug fix, and the only test here that is red against today's
code:

- After clicking a folder, the detail query `["recording", "rec-1"]` is invalidated.
- The same after create-and-move.

These assert against a spy on the `QueryClient`'s `invalidateQueries`, checking the actual key
argument. They must not be written as a guard that depends on a method being absent from the
`vi.mock` factory - that shape passes for the wrong reason and breaks silently when the mock grows.
Mutation-check both: revert the invalidation line and confirm each fails.

**`RecordingDetail.test.tsx`**:

- The Change Folder button renders when the recording has a placement in the current room.
- Clicking it opens the folder picker dialog.
- Neither button nor chips render when the recording has no placement in the current room.

**Layout caveat.** jsdom computes no geometry, so none of these prove the button actually sits to
the left of the chips or that the row does not wrap badly. That is a visual claim and will be
confirmed in the running app, not asserted in a unit test.

## Release chores

- **i18n:** new `changeFolder` key in `workspace.json` for all four locales (`en`, `de`, `es`,
  `fr`). Plain hyphens only - no em or en dashes in user-facing strings.
- **Version:** functional enhancement, so minor +1 and build reset: `0.205.3` -> **`0.206.0`** in
  `version.json` and its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
- **Release notes:** one new `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` at `0.206.0`.
  The `pr` field must be the real PR number - the previous entry is PR 507, but the next number is
  not reliably 508 (issues and Dependabot share the sequence) and no test catches a wrong one.
  Confirm it against the created PR.
- **Docs:** no README / `docs/features.md` / `CAPABILITIES` change. The ability to move a recording
  between folders already exists and is already documented; this surfaces an existing capability in
  a second place rather than adding one. No schema or architecture change, so `Data_Schema.md` and
  `Overall_Synopsis_of_Platform.md` are untouched.
- **Help content:** not a fourth sync target, and the behaviour a user relies on (how moving works)
  is unchanged. No help article edit.

## Deployment surface

**Server redeploy only.** The change touches `apps/web` alone - no desktop shell files
(`apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js`, desktop deps), so no
desktop release is needed. Installed desktop apps pick this up automatically, since the shell loads
the web app from the server origin.

## Files touched

| File | Change |
|---|---|
| `apps/web/src/pages/RecordingDetail.tsx` | Wrapper + button; pass `currentSectionId` to the modal |
| `apps/web/src/components/detail/FolderChips.tsx` | Drop `-mt-1` from the nav; update its doc comment |
| `apps/web/src/components/MoveToSectionModal.tsx` | Invalidate `["recording", recordingId]` in both move paths |
| `apps/web/src/components/MoveToSectionModal.test.tsx` | New invalidation tests |
| `apps/web/src/pages/RecordingDetail.test.tsx` | New button render/open tests |
| `apps/web/src/locales/{en,de,es,fr}/workspace.json` | `changeFolder` |
| `version.json` + 4 mirrors, `apps/web/src/lib/releases.ts` | 0.206.0 + release entry |
