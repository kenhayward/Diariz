# Design: Enhanced Notes (user notes woven into Meeting Minutes)

**Date:** 2026-07-07 · **Status:** approved design, pending spec review

## Goal

Give Diariz a Granola-style "AI-enhanced notes" capability that feels native: the user jots sparse,
timestamped **notes** before, during, and after a meeting; those notes then **steer the existing Meeting
Minutes generation** and produce a provenance-rich **Enhanced notes** section inside the minutes. Not a
separate feature - an enhancement of minutes. The forensic angle exceeds Granola: every note line carries a
recording-clock timestamp, every AI expansion carries clickable **[mm:ss] transcript deep-links**, and
nothing the user wrote is ever silently dropped.

## Decisions (locked)

- **Surfaces:** before (calendar event) + during (live, timestamped) + after (recording detail).
- **Minutes weave:** notes steer **every** prompt-driven template section AND a new `notes` field renders a
  dedicated **Enhanced notes** section.
- **Provenance:** full - the user's literal words visually distinct (bold) from AI expansion (plain), with
  `[mm:ss]` transcript deep-links and per-line capture timestamps; unexpandable lines kept verbatim with a
  "not discussed" marker.
- **Data model:** first-class `MeetingNote` entity, one row per note line, anchored to a recording **or** a
  calendar event, adopted onto the recording when the calendar link forms. (Rejected: notes-as-attachment -
  no line structure/timestamps; notes-as-pseudo-segments - pollutes the transcript model.)

## Vocabulary

- **Note / note line** - one user-authored line of text (a trigger phrase, question, observation).
- **Captured-at** - the note's offset (ms) into the *recorded* clock; null for pre-meeting or post-hoc lines.
- **Enhanced notes** - the minutes section produced by expanding each note line from the transcript.

## Data model

New entity **`MeetingNote`** (table `MeetingNotes`), one row per line:

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner; cascade on user delete |
| `RecordingId` | uuid FK → Recordings, **null** | set once anchored to a recording; cascade on recording delete |
| `CalendarId` | varchar(256) null | pre-meeting anchor (with `EventId`); cleared on adoption |
| `EventId` | varchar(256) null | pre-meeting anchor |
| `Text` | varchar(2048) | the note line (normalized via `TranscriptText.Normalize`-style trimming; no blank lines) |
| `CapturedAtMs` | bigint null | offset into the recording clock (from `recorderTiming`, which excludes pauses - aligns with the transcript for free); null = pre-meeting/post-hoc |
| `Ordinal` | int | display order within the anchor |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Constraints/indexes: `(RecordingId, Ordinal)` index; `(UserId, CalendarId, EventId)` index for event lookup.
A row is anchored to **either** a recording **or** a `(CalendarId, EventId)` pair - enforced in the
controller (not a DB check; the adoption transition sets `RecordingId` and clears the event keys).

**Adoption.** When a `RecordingCalendarLink` is created - the existing single chokepoint for both the
auto-match and the manual link - the owner's event-anchored `MeetingNote` rows for that `(CalendarId,
EventId)` get `RecordingId` set (and event keys cleared), ordered after any lines already on the recording.
Unlinking does NOT detach notes (they were about the meeting the recording captured). If the recording links
to a different event later, that event's notes are also adopted (append).

Migration: `AddMeetingNotes`. Docs: `Data_Schema.md` (table + migration history),
`Overall_Synopsis_of_Platform.md` (feature + flow), `CAPABILITIES` + release notes per PR.

## API surface

All owner-scoped by the JWT `NameIdentifier` (and reachable with a personal API token, like every `api/*`
endpoint):

- `GET /api/recordings/{id}/notes` - the recording's lines, ordinal order.
- `POST /api/recordings/{id}/notes` - bulk append `[{text, capturedAtMs?}]` (used by the live panel's
  attach-on-upload and by single-line adds; server assigns ordinals).
- `PUT /api/recordings/{id}/notes/{noteId}` - edit text (timestamps are capture facts - not editable).
- `DELETE /api/recordings/{id}/notes/{noteId}`.
- `GET/POST/PUT/DELETE /api/calendar/events/{calendarId}/{eventId}/notes` - same shape for event-anchored
  lines (pre-meeting). `calendarId`/`eventId` are URL-encoded path segments.

DTO: `MeetingNoteDto(Id, Text, CapturedAtMs, Ordinal, CreatedAt)`.

## Capture surfaces

### Before - calendar event notes
The calendar-event preview page (`/calendar-event/:id`) gains a compact **Notes editor**: a list of plain
lines + an add box, event-anchored via the calendar endpoints above. Works for any Google event the user can
see (ICS feeds excluded - they are display-only by design). Deliberately NOT on the Overview's linked-meeting
panel: there the event is already linked, so its notes have been adopted onto the recording - an event editor
would write lines that are never adopted (adoption fires only when the link forms); the Notes tab is the
post-link home.

### During - LiveNotesPanel
A notes panel that opens alongside the recorder:
- **Trigger:** a "Notes" toggle on the TopBar recorder; **auto-opens when recording starts** (dismissable;
  remembered in localStorage). Tray-started recordings behave identically (the desktop shell loads the web UI
  and drives the same recorder).
- **Behaviour:** a single text box; **Enter commits a line**, stamping it with the current recorded-ms from
  `recorderTiming` (pause-aware). Committed lines list above with their `mm:ss` stamps; editable/deletable
  before upload.
- **Durability:** lines are held in memory and mirrored to **IndexedDB** alongside the `pendingRecording`
  blob (same keying), so a crash/session lapse loses nothing; recovered together with the recording.
- **Attach:** after `POST /api/recordings` succeeds, the panel bulk-POSTs its lines to
  `/api/recordings/{id}/notes` and clears the IndexedDB mirror. If the bulk POST fails, lines stay in
  IndexedDB and a retry banner is shown (same recovery pattern as pending recordings).
- The panel is **web-side state only** before upload - no server round-trips during the meeting.

### After - Notes tab
A new **Notes** tab on the recording detail page (between Actions and Speakers): the line list with `mm:ss`
stamps (stamped lines are clickable → the existing `?t=` transcript jump), add/edit/delete, and a
**Re-create minutes** affordance when notes changed after minutes were generated. Uploads (no live session)
start here. Adopted pre-meeting lines show without a stamp.

## The minutes weave

`IMeetingTypeMinutesGenerator.GenerateAsync` gains `IReadOnlyList<MeetingNoteDto> notes` (peer of
`actions`), threaded by `MeetingMinutesProcessor` (which loads the recording's notes) into
`MinutesComposition`. Two effects:

### 1. Steering (all prompt blocks)
When notes exist, the shared section preamble (`prompts/minutes-section-preamble.md` mechanism) is extended
with a **note-taker's emphasis block**: the note lines (with timestamps where present) plus an instruction -
"the note-taker flagged these points during the meeting; give them weight, resolve them specifically from
the transcript, and prefer their terminology." Both `SingleCall` and `PerSection` strategies inherit it
because it rides the preamble. **No notes → the preamble is unchanged** (zero regression, byte-identical
prompts).

### 2. Enhanced notes section (new `notes` field)
A new resolvable field name **`notes`** (peer of `action_items`) usable in any template's field block.
Because expansion needs the LLM but field substitution must stay deterministic, the generator runs a
pre-pass when (and only when) the template contains a `notes` field AND the recording has notes:

- **`NotesEnhancer`** - one LLM call: input = the note lines (text + capturedAtMs) and the segment
  transcript (speaker/timestamped, same shaping as the existing prompts, within the char budget); output =
  strict JSON, per input line: `{noteIndex, expansion, segmentTimestampsMs: []}` - or an explicit
  `notDiscussed: true`. Every input line MUST appear in the output; the enhancer validates and repairs
  (any missing line becomes `notDiscussed`). Pure prompt-builder + response-parser (unit-testable without
  the LLM, the existing house pattern).
- **Deterministic rendering** (`NotesComposer`, pure): for each line in ordinal order -
  - `- **<user's literal text>** *(mm:ss)* - <AI expansion> [mm:ss](/recordings/{id}?t={ms}) [mm:ss](...)`
  - not discussed: `- **<user's literal text>** *(mm:ss)* - *not discussed in the recording*`
  - Bold = the user's words, plain = AI, italic stamps = capture time, links = supporting transcript moments
    (the existing deep-link convention the web already intercepts). The user's text is rendered verbatim
    (Markdown-escaped), never paraphrased.
- **Fallbacks:** template has `notes` field but recording has no notes → the section renders a single line
  "No notes were taken for this meeting." Enhancer call fails → the section renders the raw note lines with
  stamps (no expansion) and generation continues - a notes failure must never fail the minutes.

### Template integration
- `notes` joins the valid field-name list (validation + the web template editor's field picker + i18n label).
- The **seeded General template** gains an "Enhanced notes" H2 section with the `notes` field. Seeding is
  insert-if-missing by `Key` today (admin edits survive); this needs a one-time additive pass: on startup, if
  the seeded General template exists AND has no `notes` field AND has never been admin-edited (content equals
  the previous seed), replace with the new seed - otherwise leave it alone (documented behaviour; admins who
  customised General add the section themselves in the editor).
- Other seeded/custom templates are untouched; users add the section where they want it.

### Regeneration
Existing flows unchanged: apply-meeting-type and re-create-minutes automatically include notes when present.
The Notes tab's "Re-create minutes" calls the existing generate endpoint. `IsUserEdited` protection is
respected exactly as today.

## Provenance & forensic guarantees (summary)

1. The user's literal note text always appears verbatim (bold) - expansion never rewrites it.
2. Every expanded claim links to the transcript moment(s) it came from.
3. Lines the transcript doesn't support are kept and explicitly marked "not discussed" - never dropped.
4. Capture timestamps are immutable facts (editable text, non-editable stamps).
5. Notes never modify the transcript; the transcript never modifies notes.

## Non-goals (YAGNI)

Collaborative/multi-user notes; a native tray notepad (the web panel covers tray-started recordings); a
chat/MCP `get_meeting_notes` tool (future - trivially added via `IChatTool` later); retroactive minute
backfill; per-line privacy; ICS-event notes; note attachments/rich text (plain lines only).

## Delivery (three PRs, each independently shippable)

1. **PR 1 - Notes capture (recording + calendar).** `MeetingNote` + migration, recording + event notes CRUD,
   adoption at the calendar-link chokepoint, **Notes tab**, event-notes editor on the preview page +
   Overview panel. Minutes untouched. Minor bump.
2. **PR 2 - Live capture.** `LiveNotesPanel` (recorder toggle + auto-open, Enter-commits with recorded-ms
   stamps, IndexedDB mirror, attach-on-upload + retry). Web-only. Minor bump.
3. **PR 3 - The minutes weave.** Generator/processor threading, steering preamble, `NotesEnhancer` +
   `NotesComposer`, `notes` field (validation + editor picker + i18n), General-template seeding pass,
   Notes-tab "Re-create minutes" affordance. Minor bump.

All are **server redeploy (web + API)**; PR 1 adds the migration; no worker rebuild; no desktop release
(recorder UI is web-side).

## Testing (house TDD pattern)

- **Unit:** note normalization/ordering; adoption logic (pure part extracted); `NotesEnhancer` prompt
  builder + JSON parser (incl. repair of missing lines); `NotesComposer` rendering (bold/stamps/links/
  not-discussed/escaping); steering preamble composition (with/without notes - byte-identical without);
  controller CRUD + ownership (in-memory provider).
- **Integration (real Postgres):** notes round-trip; adoption on calendar-link creation; cascade on
  recording/user delete.
- **Web (vitest):** Notes tab CRUD + timestamp-click navigation; LiveNotesPanel commit/stamp/recover/attach
  (fake IndexedDB, step-driven timing - the `pendingRecording`/`InputLevelMeter` test patterns); event-notes
  editor; template editor shows the new field.
- **Live verification:** record with live notes → upload → transcribe → generate minutes with a
  notes-bearing template → confirm steering + the Enhanced notes section with working deep-links; pre-meeting
  notes adopt on auto-link.

## Risks / open edges (accepted)

- **Clock alignment** is best-effort: `recorderTiming` excludes pauses, matching the uploaded audio's
  timeline; small drift vs Whisper timestamps is acceptable (links land near, not necessarily on, the word).
- **Adoption is one-way and additive**; re-linking to a different event appends that event's notes. Simple,
  predictable; revisit only if users ask.
- **General-template seeding** upgrade rule (replace only if never admin-edited) is conservative; documented.
- The `NotesEnhancer` is one extra LLM call per minutes run (only when a `notes` field is present and notes
  exist) - bounded, and consistent with the PerSection cost model.
