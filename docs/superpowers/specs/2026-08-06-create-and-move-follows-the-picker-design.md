# Create-and-Move Follows the Picker

**Date:** 2026-08-06
**Status:** Design, implemented in the same PR

## 1. Goal

In the "Move to section" modal, creating a folder should put it **where you are looking**, not always at the top level.

## 2. The problem

The modal's create-and-move form calls `api.createSection(name, null, roomId)` - `null` parent, so always top level. That was tolerable when the folder list was flat and had no notion of "where you are". It is not tolerable now: the modal shows a picker with a visible drill position, so a user can be sitting inside `Customers > Acme Corp`, type "Project Falcon", and get a **top-level** folder called Project Falcon.

The mismatch is not new, but Phase 5 made it visible. That is why it was deferred out of Phase 5 rather than fixed there - it is a behaviour change needing its own tests, not part of swapping a list for a picker.

## 3. What has to change

**`FolderPicker` must report its drill position.** It deliberately owns that state internally, and Phase 5 held the line on not widening its contract for a single caller. That restraint was right then; this is the task that justifies the change.

The minimal form is additive: an optional `onDrillChange?: (sectionId: string | null) => void`. The picker keeps owning the state and merely reports it. It does **not** become controlled - that would be a bigger change, and no caller needs to *set* the drill position.

**The modal creates at that position.** `createSection(name, drillId, roomId)` instead of `null`.

## 4. Two things that fall out, and must not be missed

**The depth cap.** Folders nest to 8 levels and the server rejects a create beyond that with a 400. A user drilled to depth 8 who types a name would get a raw error. The nav already solved this: `sectionCreateTarget(sections, sectionId)` returns `root`, `child`, or `blocked`, and `blocked` covers both the cap and an id no longer in the tree. The modal uses the same function, and disables the create form with the existing `newSectionNestCapped` string rather than inventing a second rule or a second message.

**The form must say where it will create.** A form that silently changes what it does as you drill is worse than one that always does the same thing. The existing `newSubSectionPlaceholder` ("New sub-section in {{parent}}") already carries the parent name and is used by the nav for exactly this - reuse it, with `newSectionPlaceholder` at the root.

## 5. Non-goals

- **Making the picker controlled.** Nothing needs to set the drill position from outside.
- **Changing where the picker starts.** It still opens at the root; Phase 5 added a "Selected: {path}" line for when the current selection is not on screen.
- **Touching the other consumer.** `RecordingsSection` has no create form.

## 6. Testing

- Creating while drilled into a folder passes that folder as the parent, and the recording lands in the new folder.
- Creating at the root still passes `null` - the existing behaviour must not regress.
- At the depth cap the form is disabled with the reason shown, and no request is made.
- The placeholder names the folder you are in, and does not when you are at the root.

The first test is the one that would pass spuriously if written carelessly: the fixture must drill somewhere whose id is **not** `null`, or it cannot distinguish "follows the drill" from "always null".

## 7. Release

Functional enhancement: **Minor +1**, `0.182.0` -> `0.183.0`, across `version.json` and its four mirrors plus a `RELEASES[0]` entry. No schema change, no migration, no server change.

Deployment surface: **server redeploy only.**
