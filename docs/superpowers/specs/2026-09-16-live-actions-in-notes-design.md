# Live actions in the notes popover - design

Status: approved 2026-09-16. Plan: `docs/superpowers/plans/2026-09-16-live-actions-in-notes.md`.

## Problem

While a meeting runs, the notes popover (`components/hub/NotesPopover.tsx`, and the desktop pop-out
window that shares its body, `LiveNotesStream`) lets the user write notes. There is no way to record an
**action** as it is agreed. The user wants actions typed mid-meeting to be **adopted** - that is, pinned
(`RecordingAction.Pinned = true`), so they reach the Actions tab and the folder Actions tab - rather than
merely logged on the recording's own page.

## The trap this design exists to avoid

Adding an action by hand today (`POST api/recordings/{id}/actions`) sets `Recording.ActionsExtractedAt`,
and `ActionsProcessor` skips extraction whenever that is set. Attaching live actions through the existing
endpoint would therefore mean **one typed action suppresses every action the AI would have found**, and
the minutes' action table (which renders the canonical action list) would contain only what was typed.

## Decisions (user-approved)

1. **Extraction still runs and merges.** Live actions stay; AI-extracted items are appended after them,
   unpinned as today, skipping duplicates of the live ones.
2. **Entry:** a Note/Action toggle on the composer (Alt+A while the composer has focus) **and** a
   "Make action" control on any existing note line (plus "Make note" to reverse it).
3. **Fields:** text is enough to add an action. Owner and due date are optional, editable in the row.
   **Owner defaults to the current user** (their full name from the token, falling back to empty).

## Design

### Server

- `RecordingAction` gains two additive columns:
  - `Source` (`ActionSource` int enum: `Extracted = 0`, `Manual = 1`, `Live = 2`; append only, never
    renumber). Default `Extracted` on existing rows. Hand-adds via the existing endpoint write `Manual`.
  - `CapturedAtMs` (`long?`): offset into the recorded clock when a live action was typed. Null for
    everything else. Not user-editable.
  Both additive with defaults, so older backups restore cleanly - **no `CurrentFormat` bump**.
- New endpoint `POST api/recordings/{recordingId}/actions/live`, body
  `{ actions: [{ text, actor?, deadline?, capturedAtMs? }] }`. Owner only (404 otherwise). Blank text
  skipped, text truncated to 2048, ordinals continue after existing rows. Rows are created with
  `Pinned = true`, `Source = Live`. It does **not** touch `ActionsExtractedAt`. Returns the created DTOs.
- `ActionsPrompt.BuildMessages` gains `alreadyRecorded` (texts). When non-empty, the user message is
  suffixed with an "already recorded during the meeting - do not repeat these" list. This lives in the
  user message, not the template, because `extract-actions.md` is admin-editable and would otherwise need
  a new placeholder.
- New pure `ActionMerge.WithoutDuplicates(extracted, existingTexts)`: drops extracted items whose
  normalised text (lower-case, punctuation removed, whitespace collapsed) equals an existing one, and
  duplicates within the extraction itself.
- `ActionsProcessor` (the pipeline): no longer clears the list. Passes the existing live actions' texts as
  `alreadyRecorded`, appends the deduplicated extraction after the existing max ordinal, sets
  `ActionsExtractedAt`. The `recording.action_items_ready` webhook carries the **full** list (live +
  extracted), so a subscriber sees every action the meeting produced.
- `RecordingActionsController.Extract` (explicit re-extract): replaces only **unpinned** rows. Pinned rows
  are kept, passed as `alreadyRecorded`, and deduplicated against. The endpoint description and the web
  confirm text change to say so. This also protects pinned AI-extracted actions - a deliberate, release-noted
  behaviour change.
- `RecordingsController` merge copies `Source`; `CapturedAtMs` is copied for the survivor's own rows only
  (the other recordings' offsets are into a different clock, so they are nulled).
- `RecordingActionDto` is unchanged (the new columns have no consumer on the API surface yet).

### Web

- `LiveNoteLine` (in `lib/types.ts`) extends `MeetingNote` with optional `kind?: "note" | "action"`,
  `actor?: string`, `deadline?: string`. Absent `kind` means note, so every existing `MeetingNote` value
  and every stash written before this change remains valid.
- `useLiveNotes`: `add(text, atMs?, kind?)` (actions get `actor = defaultActor()`), `setKind(id, kind)`
  (promoting a note fills the default owner if none), `updateAction(id, { actor?, deadline? })`. The
  IndexedDB mirror stores the new fields.
- `notesStream`: new item kind `action` (id prefix `a:`), new filter `actions`, `streamCounts` returns
  `actions`; the `notes` count and filter exclude actions.
- `LiveNotesStream`: composer mode toggle (button with `aria-pressed`, Alt+A on the input); the mode
  resets to Note after an action is filed, so a forgotten toggle does not turn later notes into tracked
  actions. New Actions chip. `ActionRow` (in `notesStreamRows.tsx`) shows text, owner chip, due date,
  edit, a details editor for owner/due, "Make note", delete. `NoteRow` gains "Make action".
- Pop-out: `notesChannel` carries `kind` on `add`, plus `setKind` and `updateAction` messages;
  `useNotesPopout`, `NotesPopover` and `pages/NotesPopout.tsx` pass them through.
- `Recorder.attachNotes` splits lines: notes go to `api.createNotes`, actions to the new
  `api.createLiveActions`. Each part that succeeds is removed from what the retry stash keeps, so a retry
  never duplicates the part that already landed.

### Not in scope

- A system-wide hotkey for actions (would need a desktop release).
- Showing `Source`/`CapturedAtMs` in the UI (a "added live" badge, jump-to-moment from an action).
- Fuzzy/semantic dedupe beyond the prompt context plus normalised-text backstop.
- Deduplicating live actions attached *after* extraction already ran (a late retry): they are appended.

## Testing

TDD throughout. .NET unit tests for the endpoint, prompt, merge helper, processor merge, re-extract
keeping pinned, and the merge copy; an integration test for the migration round-trip on real Postgres.
Web tests for the stream builder, hook, channel, composer toggle and Alt+A, rows, and the recorder's split
attach with partial failure.

## Release

Feature: minor bump to `0.272.0`. README Features row, `docs/features.md`, `CAPABILITIES`,
`Data_Schema.md` (columns + migration history), help articles `recording-audio.md` and
`action-items.md`. OpenAPI snapshot and the n8n generated index regenerate (new public endpoint).
Server redeploy only - no desktop release.
