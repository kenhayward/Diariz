# Live Notes Panel: One Stream - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Notes / Transcript tabs in the "Notes while recording" panel with one stamped stream - notes, screen captures and live transcript lines interleaved on a single timeline - with a composer docked at the bottom, a **Use in chat** action that puts the running meeting into the chat prompt, a per-capture **Chat** button plus drag-to-chat, and global hotkeys in the desktop shell. Both hosts - the inline popover and the detached `/notes-popout` window - render the same panel.

**Design contract:** [`docs/superpowers/specs/2026-09-03-live-notes-one-stream/README.md`](../specs/2026-09-03-live-notes-one-stream/README.md) (the Claude Design handoff, copied verbatim; screenshots in `screens/`). The interactive `.dc.html` prototypes stay in the handoff zip - they are references, not code to copy. Every pixel value in the handoff is quoted with the `--hub-*` token it came from; **use the tokens**.

**Architecture:** The stream is a *derived* list (`lib/notesStream.ts`, pure), merged from three things the host already owns - `useLiveNotes` lines, the `PendingShot` stash and `useLiveTranscript` segments - and rendered by one new component, `components/hub/LiveNotesStream.tsx`, that both `NotesPopover` and `pages/NotesPopout` wrap. The pop-out remains a remote control: every new action it offers (pin a stamp, send a capture or the transcript to chat, toggle on-top, compact) is a message over `notesChannel`, and the host does the work. Two host-side additions make the chat actions possible at all: captures taken under a live session are **uploaded as they are taken** (today they wait for Stop), and the chat panel gains a **sticky live-meeting context** that references the recording by id rather than pasting transcript text.

**Tech Stack:** React 19 + TypeScript + Vite + Tailwind v4, vitest + @testing-library/react, react-i18next (4 catalogues); Electron shell (`apps/desktop`, `node --test`). No API or schema change.

**Split into three PRs, each one release.** They build on each other in order, and only the third needs a desktop release:

| PR | Ships | Deployment surface |
|---|---|---|
| A - One stream | The merged stream, chips, composer with pinned stamp, elapsed clock, both hosts rewritten. No chat, no hotkeys. | server redeploy |
| B - Into the chat | Eager capture upload under a live session; Use in chat; capture Chat button + drag; pop-out relays. | server redeploy |
| C - Hands off the keyboard | On top toggle, Compact, window sizing, three global hotkeys and their routing, hint line. | **desktop release** (`v*` tag) + server redeploy |

---

## Decisions that depart from the handoff (read first)

The handoff was written against the prototype, not the code. Reviewing the code turned up seven places where following it literally would not work or would be worse. **Items 1-3 change behaviour the user will notice; confirm them before starting PR B / PR C.**

1. **"Use in chat" attaches the recording, not a text snapshot.** The handoff says to send "the transcript text so far, marked as provisional, as sticky chat context". Two things argue against pasting text. First, the chat's single context pill is *single-origin* (`ChatPanel.mergeAttachment`): a transcript pasted as text would either accumulate a second copy on every press or fight the OCR/file pill with a confirm dialog. Second, the server already does the framing the handoff asks for: `ChatContextBuilder.cs:34` prefixes any recording in status `Live` with "This meeting is IN PROGRESS and still being recorded", and a live session creates the recording on the server from the first second (`POST /api/recordings/live`), so the transcript the chat reads is *always current*, not a stale paste. So: `attachLiveRecordingToChat(recordingId)` (a third channel in `lib/chatAttachments.ts`), a sticky "Live meeting" pill in `ChatPanel`, and the id merged into `recordingIds` on send. The button is hidden when there is no live transcript, exactly as the Transcript tab is today - without a live session there is no server-side transcript to reference *and* nothing to paste.

2. **A capture can only go to chat once it exists on the server.** `ChatScreenshotRef` is `{recordingId, screenshotId}`; the server loads the pixels from object storage. Today captures are `PendingShot` blobs in IndexedDB until `attachScreenshots` runs after the audio upload at Stop (`Recorder.tsx:780`). Under a live session the recording already exists and the screenshot endpoint's only gate is ownership (`ScreenshotsController.Create`), so PR B **uploads each capture as it is taken when `liveRef.current` is set**, records the returned id on the shot (`serverId`), and attach-on-stop skips those. Chat / drag are offered only on a capture with a `serverId`; otherwise the Chat button takes a `disabledReason` (uploading, or no live session). Deleting an uploaded capture also deletes the server copy.

3. **Hotkeys: keep the existing configurable capture hotkey, and pick collision-safe defaults for the two new ones.** The shell already has a *user-configurable* capture hotkey (default `CommandOrControl+Shift+9`, `hotkey.html`, registered only while recording - `main.js applyShortcut`). The handoff's `Ctrl+Shift+S` would replace it, and its `Ctrl+Shift+N` / `Ctrl+Shift+C` are global grabs of Chrome's incognito window, Explorer's new folder, and copy in Windows Terminal / VS Code - held for the whole meeting. Recommendation: capture stays on the stored `captureHotkey`; note and transcript-to-chat become two more stored accelerators (`noteHotkey`, `transcriptChatHotkey`) defaulting to **`CommandOrControl+Shift+0`** and **`CommandOrControl+Shift+8`**, beside the existing 9. The hint line renders the *actual* accelerators from the store, formatted per platform, so the copy is never wrong. **User decision:** accept these defaults, or take the handoff's letters knowingly. Extending `hotkey.html` to three rows is Task 13b (optional).

4. **The status line gets short strings.** The handoff says to reuse `liveTranscriptBehind` / `liveTranscriptLive` / `liveTranscriptDegraded`, but those are full sentences ("Transcribing as you talk, about 16s behind. This text is not final." - 67 chars) and the design's line is 11px, single-line, sharing its row with a confirmation. Add three short keys (`liveStatusLive`, `liveStatusBehind`, `liveStatusPaused` - "Live", "Live - transcript {{seconds}}s behind", "Live transcript paused") for the line and keep the existing long strings as its `title`, so the caveat survives on hover.

5. **Pop-out window sizes.** The handoff asks for a 420x740 *minimum*. That does not fit a 768-tall laptop display with a taskbar. Default becomes 420x740, minimum 360x480 (`notesWindowState.js`). Compact mode is a separate, temporary size (Task 12).

6. **Small design gaps, resolved here:** (a) *Releasing a pinned stamp without filing a note* - clicking the pinned badge releases it (it becomes a button, `aria-label` "Follow the clock again"); Escape cannot be used because `HubPopover` closes on Escape. (b) *Stamp column width* - 34px fits `mm:ss`; past one hour `formatDuration` renders `h:mm:ss`, so the column is 50px whenever the elapsed clock is over an hour (one prop, not per-row measurement). (c) *Icon buttons stay 28px* - the handoff allows it, and the shared `HubIconButton` stays untouched. (d) *Characters* - "Diariz — live notes" becomes "Diariz - live notes" (`noFancyDashes.test.ts`), "Note this moment…" uses three dots, the "＋" is an SVG plus glyph like every other icon.

7. **Cross-window drag needs a manual check.** Dragging a thumbnail from the pop-out `BrowserWindow` onto the main window's composer is a Chromium-to-Chromium HTML5 drag between two windows of one app; it is expected to work but nothing in the test suites can prove it. The pop-out's Chat *button* does not depend on it (it relays through the channel), so drag failing there degrades to "use the button". Verify on Windows before closing PR B.

---

## Global Constraints

- **TDD is mandatory.** No production code without a failing test that preceded it. Every guard must be **mutation-verified**: after it passes, introduce the exact regression it exists to catch, watch it fail, revert.
- **Never `git add -A`.** Stage explicit paths only.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only. `apps/web/src/lib/noFancyDashes.test.ts` enforces it across UI strings, all four locale catalogues, release notes and help. Code, comments and internal docs are exempt.
- **Help articles are ASCII only** (`content/help/helpContent.test.ts`).
- **Never put production data anywhere in the repo.** Invent fixture names (`Ada`, `Grace`, `Sam`, `Priya`).
- **`main` is branch protected.** Work lands via a PR. Never commit or push to `main`, never merge locally. Push the branch and `gh pr create` without asking.
- **Branches:** `claude/live-notes-one-stream`, `claude/live-notes-chat`, `claude/live-notes-shell` off `origin/main`, each after the previous PR merged.
- **Target versions:** each PR is a functional enhancement - **minor +1, build reset to 0**. `version.json` is `0.267.0` at the time of writing; PR A is `0.268.0` *if nothing else ships first* - confirm, do not assume. Bump `version.json` **and all seven mirrors** (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`, and the three `package-lock.json` files, two `version` fields each). `versionMirrors.test.ts` fails the build if one drifts.
- **The `pr:` field is a guess until the PR exists.** Write it, open the PR, then correct it.
- **The release-notes file is `apps/web/src/lib/releaseNotes/current.ts` (`RECENT`).** The About-box `CAPABILITIES` is in `lib/appInfo.ts`. Never import `releaseNotes/archive.ts` anywhere new.
- **No `MaintenanceController.CurrentFormat` bump** and no `Data_Schema.md` edit: nothing here touches Postgres or MinIO. (`serverId` on a stashed shot is browser-side IndexedDB.)
- **All four locale catalogues** (`de`, `en`, `es`, `fr`) carry every existing notes/screenshot key; add every new key to all four.
- **The web suite cannot be trusted locally on Windows.** Before claiming it passes, run it on Linux: `docker run --rm -v <repo>:/src:ro node:24 bash -c 'mkdir -p /work && cd /work && tar -C /src -cf - --exclude=node_modules --exclude=.git --exclude=bin --exclude=obj --exclude=omi . | tar -xf - && cd apps/web && npm ci && npx vitest run'`. Copy the **whole tree**: `vitest.config.ts` reads the repo-root `version.json` and the mirror/dash tests read files outside `apps/web`.
- **`act()` discipline:** `src/test-setup.ts` fails any test that breaks the `act` contract. Resolve elements first, then `act` on them; never await a query inside an `act` scope. Fake timers for the 2.6s / 2.4s confirmations and the 1s clock (`vi.useFakeTimers()` + `act(() => vi.advanceTimersByTime(...))`).
- **Expect one intermittent failure on the first run after a rebuild** on the web suite that never reproduces. Re-run before investigating.

**Commands:**

```bash
cd apps/web && npx vitest run src/lib/notesStream.test.ts src/components/hub/LiveNotesStream.test.tsx
```

```bash
cd apps/web && npx vitest run src/components/hub/NotesPopover.test.tsx src/pages/NotesPopout.test.tsx src/lib/notesChannel.test.ts
```

```bash
cd apps/desktop && npm test
```

---

# PR A - One stream

### Task 1: `lib/notesStream.ts` - the derived stream

The whole redesign rests on one pure function. Everything a row needs to render - its kind, its stamp, whether a transcript line shows its speaker - is decided here, so the components stay dumb and the interesting rules are tested without React.

**Files:**
- Create: `apps/web/src/lib/notesStream.ts`
- Test: `apps/web/src/lib/notesStream.test.ts`

**Shape:**

```ts
export type StreamFilter = "all" | "notes" | "captures";
export type StreamItem =
  | { kind: "transcript"; id: string; atMs: number; segment: LiveSegment; showSpeaker: boolean }
  | { kind: "note"; id: string; atMs: number; note: MeetingNote }
  | { kind: "capture"; id: string; atMs: number; shot: ShotView };
export function buildStream(input: {
  lines: MeetingNote[]; shots: ShotView[]; segments: LiveSegment[]; filter: StreamFilter;
}): StreamItem[];
export function streamCounts(input): { notes: number; captures: number };
export function stampColumnPx(elapsedMs: number): 34 | 50;
```

- [ ] Sorted by `atMs` ascending; ties keep transcript before note before capture, then insertion order (stable) - a note filed at the same second as the line it was about reads *after* the line.
- [ ] A note with `capturedAtMs: null` (a pre-meeting line adopted from a stash) sorts first, at 0.
- [ ] `showSpeaker` compares with the **previous transcript segment in transcript order**, not the previous stream item - a note or capture between two lines from the same speaker must not make the name repeat. Port the four speaker cases from `LiveTranscriptPanel.test.tsx` (shows who is speaking / suggestion / repeats only on change / no speaker at all).
- [ ] `filter: "notes"` returns only notes; `"captures"` only captures; `"all"` everything. Counts are computed from the *unfiltered* input (the chips show live totals whichever is selected).
- [ ] `stampColumnPx` is 34 under one hour, 50 at or over it.
- [ ] Recomputation is the caller's job (`useMemo` on `[lines, shots, segments, filter]`) - note in the doc comment that `liveTranscript` replaces wholesale on each append (`useLiveTranscript.ts`), so this must be recomputed, never appended to.

### Task 2: A stamp the caller can pin, and a clock the pop-out can tick

Two small contract changes, both host-side, both needed before the panel can be built.

**Files:**
- Modify: `apps/web/src/lib/useLiveNotes.ts` - `add(text, atMs?)`
- Modify: `apps/web/src/lib/notesChannel.ts` - `add` message gains `atMs?`; `NotesState` gains `clock`
- Modify: `apps/web/src/lib/useNotesPopout.ts` - pass `atMs` through
- Modify: `apps/web/src/components/Recorder.tsx` - publish `clock`
- Test: `apps/web/src/lib/useLiveNotes.test.tsx`, `apps/web/src/lib/notesChannel.test.ts`, `apps/web/src/components/RecorderPopoutPublish.test.tsx`

- [ ] `useLiveNotes.add(text, atMs?: number)`: a supplied `atMs` becomes `capturedAtMs`; absent, `stampMs()` as today. Test both, and that `ordinal` still counts from the list length.
- [ ] `notesChannel`: `{ type: "add"; text: string; atMs?: number }`; `NotesHostHandlers.onAdd(text, atMs?)`; `NotesClient.add(text, atMs?)`. Test that a pinned add round-trips its stamp and an unpinned one carries none.
- [ ] `NotesState.clock: { recordedMs: number; atWallMs: number; running: boolean }`. The client derives the displayed time as `recordedMs + (running ? Date.now() - atWallMs : 0)` on a 1s interval. **Do not** put a ticking `elapsedMs` in `NotesState`: the host republishes the whole state (thumbnail blobs included) on every change, and a 250ms tick would do that four times a second for the whole meeting. The host already republishes on pause/resume because `recording`/`paused` state changes re-render `notesState`.
- [ ] `Recorder.tsx`: `clock: { recordedMs: elapsed, atWallMs: Date.now(), running: recording && !paused }`. Extend `RecorderPopoutPublish.test.tsx`: paused publishes `running: false`; the pop-out is republished with a fresh `atWallMs` on resume.

### Task 3: `components/hub/LiveNotesStream.tsx` - bands 2 to 5

The panel body both hosts share. In PR A the action row carries only the capture controls (Use in chat arrives in PR B, the hotkey line in PR C); leave the left slot as a `ReactNode` prop (`actionSlot`) so B drops the button in without touching layout.

**Files:**
- Create: `apps/web/src/components/hub/LiveNotesStream.tsx` (bands + composer + status)
- Create: `apps/web/src/components/hub/notesStreamRows.tsx` (`TranscriptRow`, `NoteRow`, `CaptureRow`, the plus/pencil/close glyphs)
- Create: `apps/web/src/components/hub/hubGlyphs.tsx` - move `IconPopOut`, `IconClose` out of `NotesPopover.tsx`, add `IconPlus`, `IconPencil` (`M4 20h4l10-10-4-4L4 16v4Z`), `IconArrowRight` (`M5 12h13M13 6l6 6-6 6`), `IconCheck` (`M5 13l4 4L19 7`), `IconChatArrow` (bubble `M21 11.5a8.4...` + `M8.5 12h7M12.5 9l3 3-3 3`), `IconPin`, `IconGrip` (`M9 5h.01M9 12h.01...`). All 24-viewBox, `stroke="currentColor"`, `strokeWidth={2}`, round caps - the `Glyph` pattern in `CaptureControls.tsx`.
- Modify: `apps/web/src/index.css` - the note-row and transcript-row hover backgrounds and the confirmation pill are theme-dependent literals with no token (`rgba(47,107,237,.08)` dark / `.06` light etc.). Follow the `.hub-tags-pill` precedent: class rules under `:root` / `.dark`, **not** inline styles, so hover is not dead code.
- Test: `apps/web/src/components/hub/LiveNotesStream.test.tsx`

**Props (PR A):**

```ts
{
  stream inputs: lines, shots, liveTranscript?, liveLagSeconds?, liveDegraded?,
  elapsedMs: number,
  onAdd(text, atMs?), onEdit, onDelete, onDeleteShot,
  capture: { captureAreaSet, autoCapture?, onToggleAutoCapture?, onCapture?, onChangeArea?, unavailableReason? } | undefined,
  disabled?: boolean,            // pop-out offline: composer inert, not hidden
  variant: "popover" | "window", // 300px fixed stream vs flex:1; 13px vs 14px input; 150x84 vs 170x96 thumbs
  actionSlot?: ReactNode,
  stampColumnPx: 34 | 50,
}
```

- [ ] **Chips** (`role="radiogroup"`, each chip `role="radio"` + `aria-checked`): "Everything", "Notes {n}", "Captures {n}"; the Captures chip is hidden when `capture` is undefined (a plain browser has no captures to filter). State line at the right end: empty / "notes only" / "captures only".
- [ ] **Stream** (`role="list"`, `data-testid="notes-stream"`): `height: 300` for `popover` (fixed, not max - the composer never moves), `flex: 1; min-height: 0` for `window`. Rows per Task 1's items. Empty states: no transcript yet reuses `liveTranscriptEmpty`; no items under a filter reuses `notesEmpty` / `screenshotsEmpty`.
- [ ] **Auto-scroll:** on every stream change, if the list was within 24px of the bottom *before* the change, scroll to the bottom after it (`useLayoutEffect`, read `scrollTop + clientHeight >= scrollHeight - 24` from a ref captured pre-render). Test in jsdom by stubbing `scrollHeight`/`clientHeight` getters on the element and asserting `scrollTop` after an append - and that a list scrolled up stays put.
- [ ] **TranscriptRow:** stamp `--hub-muted` (**not** `--hub-placeholder` - fails AA at 11px), speaker per `showSpeaker` with the italic + "?" + `title` treatment for a suggestion (`data-suggestion`), text `--hub-text-2` 13px/1.65, trailing plus button (20x20, `--hub-placeholder`, `opacity` 0 until row hover/focus-within, always in the tab order) with `aria-label` "Write a note about this moment" -> `onPin(segment.startMs)`.
- [ ] **NoteRow:** `box-shadow: inset 2px 0 0 var(--hub-blue)`, stamp `--hub-blue-text` 600, text `--hub-text` 500, pencil -> inline edit (input + Save/Cancel, same keys as `NotesSection`: `notesEdit`, `notesSave`, `notesCancel`), close -> `onDelete`. Port the edit/delete behaviour tests from `NotesPopout.test.tsx` ("routes edits and deletes to the host").
- [ ] **CaptureRow:** thumbnail from `URL.createObjectURL(shot.thumb)` - keep `ShotStrip`'s memo + revoke-on-change + revoke-on-unmount rule and its three tests (one URL per capture, revoked when the set changes, revoked on unmount); `alt` = `screenshotAlt`. Delete button (22x22, `--hub-red-text`). No drag, no Chat until PR B.
- [ ] **Status line** (`aria-live="polite"`, never `role="alert"` - the existing comment in `LiveTranscriptPanel.tsx` says why): green dot `blink 1.6s`, short string per Decision 4, long string on `title`. Hidden entirely when `liveTranscript` is undefined. A `statusSlot?: ReactNode` at its right end for PR B's confirmation.
- [ ] **Composer:** stamp badge showing `formatDuration(pinnedAtMs ?? elapsedMs)`; when pinned the badge is a `<button aria-pressed>` that releases the pin; input `placeholder` "Note this moment..." (`notesComposerPlaceholder`), `aria-label` same, `autoFocus` when `variant === "popover"`; Enter files via `onAdd(text, pinnedAtMs ?? undefined)`, clears the draft **and the pin**, keeps focus; whitespace does nothing; the return-key hint (`aria-hidden`). `disabled` disables the input and swallows Enter.
- [ ] Tests, at minimum: renders the three row kinds in stamp order; chips filter and count; plus pins the badge and focuses the input; Enter files at the pinned stamp then the badge follows the clock again; clicking the pinned badge releases without filing; Enter on whitespace does nothing; `disabled` makes the composer inert but present; speaker-name cases (ported); status strings for live / behind / paused; the empty states; the object-URL lifecycle (ported); auto-scroll at tail and not when scrolled up.

### Task 4: `NotesPopover` rewritten around the stream

**Files:**
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`
- Delete: `apps/web/src/components/hub/LiveTranscriptPanel.tsx`, `LiveTranscriptPanel.test.tsx` (its cases now live in `notesStream.test.ts` / `LiveNotesStream.test.tsx`), `apps/web/src/components/hub/ShotStrip.tsx` (only these two hosts used it)
- Test: `apps/web/src/components/hub/NotesPopover.test.tsx` (rewrite the tab tests; keep the capture-control and pop-out-control cases)

- [ ] Header per the handoff: red dot, **"Recording"** (`liveNotesRecording`, 15px/700) - not `liveNotesTitle` any more - the elapsed clock (13px/500 monospace `--hub-muted`), pop-out and close buttons. **Drop the `liveNotesHint` paragraph and the tabs.** Keep `HubPopover` `width={400}` `anchorClassName="right-0"` and the `data-testid="notes-popover"`.
- [ ] New props: `elapsedMs: number`; `onAdd(text, atMs?)`. Everything else as today. `captureAreaSet` default stays `true` for the same reason the comment gives.
- [ ] `capture` is passed to the stream only when **both** `onChangeCaptureArea` and `onCapture` are present (the existing pairing rule and its test "hides the whole screenshot section when only one capture handler is supplied").
- [ ] Tests: no `tablist` any more; a transcript line, a note and a capture render in one list; the clock renders `formatDuration(elapsedMs)`; pop-out control present/absent as before; Escape/backdrop still close (`HubPopover`, unchanged).
- [ ] `NotesSection.tsx` **stays** - `RecordingDetail` and the section pages use it with `onJump`. Only the live hosts stop importing it.

### Task 5: `NotesPopout` rewritten around the stream

**Files:**
- Modify: `apps/web/src/pages/NotesPopout.tsx`
- Test: `apps/web/src/pages/NotesPopout.test.tsx`

- [ ] 40px title bar: dot, "Diariz - live notes" (`notesPopoutTitle`, plain hyphen), the clock ticked locally from `state.clock` (1s `setInterval`; frozen while `running` is false), Close. **On top** and **Compact** are PR C - leave the slot.
- [ ] Body: `LiveNotesStream variant="window"`, `disabled={!live}`, `capture` only when `state.canCapture`, with `unavailableReason={live ? undefined : t("notesPopoutOffline")}` exactly as today.
- [ ] Waiting / disconnected copy unchanged (`notesPopoutWaiting`, `notesPopoutDisconnected`), same "only once contact existed" rule and its two tests.
- [ ] `onAdd` relays `client.add(text, atMs)`.
- [ ] Tests: the tab tests go; the clock advances with fake timers and freezes when `running: false`; pinned add reaches the host with its stamp; the offline composer is disabled not hidden; capture row disabled reason when offline.

### Task 6: Recorder wiring

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx` (around lines 912-945 and 1714-1735)
- Test: `apps/web/src/components/Recorder.test.tsx` (the notes-popover cases)

- [ ] `<NotesPopover elapsedMs={elapsed} onAdd={notes.add} .../>` - `elapsed` already ticks at 250ms while recording (`startTicker`).
- [ ] `useNotesPopout` handlers: `onAdd: notes.add` now accepts the optional stamp (Task 2).
- [ ] Nothing else changes: the auto-open preference (`NOTES_OPEN_KEY`), the hub id `"notes"`, attach-on-stop, the stashes.

### Task 7: i18n, docs and the release checklist (PR A)

**Files:**
- Modify: `apps/web/src/locales/{de,en,es,fr}/workspace.json` - `liveNotesRecording`, `notesPopoutTitle`, `notesComposerPlaceholder`, `notesFilterAll`, `notesFilterNotes`, `notesFilterCaptures`, `notesFilterNotesOnly`, `notesFilterCapturesOnly`, `notesPinToMoment`, `notesUnpin`, `notesEnterHint`; and `recordings.json` - `liveStatusLive`, `liveStatusBehind`, `liveStatusPaused`. Remove `liveNotesHint` and `liveTranscriptTab` from all four once nothing reads them.
- Modify: `version.json` + the seven mirrors; `apps/web/src/lib/releaseNotes/current.ts` (`RECENT[0]`, minor bump, `pr:` corrected after `gh pr create`).
- Modify: `apps/web/src/lib/appInfo.ts` `CAPABILITIES` - the **Live transcript** row ("a Transcript tab in the notes panel" -> the transcript runs in the notes panel's single timeline, each line stamped) and the **Notes** row (notes, captures and the live transcript on one timeline; a note can be pinned to a line said earlier).
- Modify: `README.md` Features rows **Live transcript** and **Notes** (line 32 area) and `docs/features.md` bullets at lines 91 (live transcript) and 127 (live notes panel) - all three in lockstep.
- Modify: `apps/web/src/content/help/en/live-transcript.md` (the `summary` front-matter and the "Transcript tab" prose - there is no tab now) and `recording-audio.md` "Taking notes as you go" (the pinned stamp; the separate-window section still holds).
- Modify: `docs/Overall_Synopsis_of_Platform.md` around line 3155 (the `/notes-popout` + `BroadcastChannel` paragraph): the `add` message carries an optional stamp; the state carries a clock the client ticks locally, and why.

- [ ] PR body: what shipped, "server redeploy only - no desktop release", and the Linux vitest count.

---

# PR B - Into the chat

### Task 8: Captures upload as they are taken under a live session

**Files:**
- Modify: `apps/web/src/lib/pendingScreenshots.ts` - `PendingShot.serverId?: string`, persisted (`StoredShot` includes it; a new `setPendingScreenshotServerId(userId, id, serverId)` writes just that record)
- Modify: `apps/web/src/lib/types.ts` - `ShotView.serverId?: string`
- Modify: `apps/web/src/components/Recorder.tsx` - `addLiveShot`, `deleteLiveShot`, `attachScreenshots`, `notesState.shots`
- Test: `apps/web/src/lib/pendingScreenshots.test.ts`, `apps/web/src/components/Recorder.test.tsx`

- [ ] `addLiveShot`: after the in-memory/stash write, **if `liveRef.current` is set**, `api.createScreenshot(liveRef.current.recordingId, stamped)`; on success set `serverId` on the shot in `liveShotsRef` + state and call `setPendingScreenshotServerId`. On failure (413 quota, network) leave `serverId` unset - attach-on-stop retries it exactly as today and its retry banner already covers the failure. Uploads are fire-and-forget from the capture hot path; keep them **sequential** (a small promise chain ref) so a burst from auto-capture does not open twenty multipart POSTs at once.
- [ ] `attachScreenshots`: skip shots that have a `serverId` (they are attached already), and count only the rest in `attachProgress`. The recovery path (`loadPendingScreenshots` after a crash) sees `serverId` in the stash and skips those too - **this is why it must be persisted**, otherwise a crash mid-meeting re-uploads every capture as a duplicate.
- [ ] `deleteLiveShot`: if the shot has a `serverId` and `liveRef.current` is still set, also `api.deleteScreenshot(recordingId, serverId)` (fire-and-forget; a failed delete leaves an orphan the user can remove from the Notes tab later, which beats blocking the UI).
- [ ] `notesState.shots` carries `serverId` so the pop-out can offer Chat on the right captures.
- [ ] Tests: a capture under a live session is POSTed once and gets its id; without a live session nothing is POSTed; a shot with `serverId` is skipped at stop and not counted in progress; a stashed shot with `serverId` is skipped on recovery; delete removes the server copy only when uploaded; a 413 leaves the shot local and attach-on-stop still sends it; two rapid captures upload in order.

### Task 9: A sticky live-meeting context in chat

**Files:**
- Modify: `apps/web/src/lib/chatAttachments.ts` - `attachLiveRecordingToChat(recordingId)` / `onChatLiveRecordingAttached(cb)`, a third channel, with the same "why a separate channel" reasoning as the text one
- Modify: `apps/web/src/components/ChatPanel.tsx` - `liveRecordingId` state, a pill beside the screenshot tray, removal, merged into `recordingIds` at send
- Modify: `apps/web/src/components/Workspace.tsx` - expand the right panel on attach, like screenshots
- Modify: `apps/web/src/locales/*/chat.json` - `liveMeetingContext` ("Live meeting"), `removeLiveMeeting`
- Test: `apps/web/src/lib/chatAttachments.test.ts`, `apps/web/src/components/ChatPanel.test.tsx`

- [ ] On send, `recordingIds` = current-context ids plus `liveRecordingId` if set and not already present. In `contextMode: "all"` the id is still sent (the server dedupes against the search set, and the recording is what the user asked about). `hasContext` counts it.
- [ ] Sticky like the screenshot tray: rides every turn until removed, survives navigation. It is **not** cleared when the recording ends - after Stop the same id is a finished recording, the server's framing drops the in-progress prefix on its own, and "ask about the meeting I just had" is the natural next question.
- [ ] Attaching the same id twice is a no-op.
- [ ] Tests: attach shows the pill and expands the panel; send includes the id exactly once even when it is also the current context; remove drops it from the next send; a second attach does not duplicate.

### Task 10: Use in chat, capture Chat, drag - in the stream and over the channel

**Files:**
- Modify: `apps/web/src/components/hub/LiveNotesStream.tsx` / `notesStreamRows.tsx`
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`, `apps/web/src/pages/NotesPopout.tsx`
- Modify: `apps/web/src/lib/notesChannel.ts` - client messages `shotToChat { id }`, `transcriptToChat`; host handlers `onShotToChat(id)`, `onTranscriptToChat()`; `NotesState.liveRecordingId?: string`
- Modify: `apps/web/src/lib/useNotesPopout.ts`, `apps/web/src/components/Recorder.tsx`
- Modify: `apps/web/src/locales/*/workspace.json` - `notesUseInChat`, `notesUseInChatHint`, `notesTranscriptSent`, `notesCaptureChat`, `notesCaptureSent`, `notesCaptureChatUploading`, `notesCaptureChatNoLive`, `notesDragToChat`
- Test: `LiveNotesStream.test.tsx`, `NotesPopover.test.tsx`, `NotesPopout.test.tsx`, `notesChannel.test.ts`, `Recorder.test.tsx`

- [ ] **Use in chat** (`actionSlot`): rendered only when `liveTranscript` is present. Click -> `onTranscriptToChat()`; the button is replaced in place by a `role="status"` pill "Transcript sent to chat" (green soft bg/border, tick glyph) for 2.6s, then reverts. The panel does not close; focus stays where it was. Hover uses the `.hub-tags-pill` class step (Task 3's CSS precedent).
- [ ] **Capture Chat button** (overlay bar): enabled when `shot.serverId` is set -> `onShotToChat(shot.id)`; confirmation "Capture added to chat" in `statusSlot` for 2.4s. `disabledReason` = "Still uploading" when live but no `serverId` yet, "Available once the recording is saved" when there is no live session (`HubIconButton`-style inert-by-handler so the tooltip renders).
- [ ] **Drag:** thumbnail `draggable` only when `serverId` is set; `onDragStart` sets `SCREENSHOT_DRAG_TYPE` to `JSON.stringify({ recordingId, screenshotId: serverId, capturedAtMs })` and `effectAllowed = "copy"` - the payload `ChatPanel.onDropOnComposer` already parses. The "drag to chat" pill (grip glyph) shows only when draggable. `dragHasFiles` already excludes our type, so upload zones do not light up.
- [ ] **Host side** (`Recorder.tsx`): `onTranscriptToChat` -> `attachLiveRecordingToChat(liveRecordingId)`; `onShotToChat(id)` -> look the shot up in `liveShotsRef`, `attachScreenshotToChat({ recordingId: liveRecordingId, screenshotId: serverId })`. The inline popover calls these directly; the pop-out sends the two channel messages and the host calls the same two functions. `notesState.liveRecordingId` lets the pop-out decide the disabled reason.
- [ ] Tests: Use in chat hidden without a transcript; click publishes and shows the confirmation which clears at 2.6s (fake timers); Chat button enabled/disabled reasons for the three capture states; drag payload shape and `effectAllowed`; no drag handle without `serverId`; channel routes both new messages; `Recorder` attaches the right pair; the pop-out relays rather than importing `chatAttachments` (assert the module is not imported by `NotesPopout.tsx` - a grep-style test like `bundleBoundary.test.ts`, because the pop-out has no chat panel and an in-window pub/sub there is a silent no-op).

### Task 11: Docs and the release checklist (PR B)

- [ ] Version + mirrors + `RECENT[0]` (minor bump).
- [ ] `CAPABILITIES` rows **Meeting screenshots** (a capture goes to chat *during the meeting*, from the notes panel or by dragging) and **Chat over transcripts** ("Use in chat" puts the running meeting into the prompt); README rows 37 and 40; `docs/features.md` matching bullets - lockstep.
- [ ] Help: `chat-over-transcripts.md` (a running meeting can be sent to chat from the notes panel; captures too); `recording-audio.md` screenshots section (captures now upload as they are taken when the recording is streaming).
- [ ] `docs/Overall_Synopsis_of_Platform.md`: the notesChannel message table gains `shotToChat` / `transcriptToChat`; the screenshot paragraph (~line 1416) drops "held until the audio has uploaded" and explains the eager upload + `serverId` + attach-on-stop skip. Also update the endpoint description string in `ScreenshotsController.Create` (the `EndpointDescription` says the client holds captures until Stop - that is now only true without a live session; a one-line C# string edit, no behaviour change).
- [ ] **Manual check before opening the PR** (Decision 7): drag a thumbnail from the pop-out window onto the main window's chat composer on Windows; note the result in the PR body.
- [ ] PR body: "server redeploy only".

---

# PR C - Hands off the keyboard

### Task 12: On top toggle and Compact in the pop-out window

**Files:**
- Modify: `apps/desktop/src/notes-preload.js` - expose `setAlwaysOnTop(flag)`, `setCompact(flag)`, `onNotesCommand(cb)`
- Modify: `apps/desktop/src/main.js` - `ipcMain.handle("notes:set-always-on-top")`, `("notes:set-compact")`; `trackBounds` ignores bounds while compact
- Modify: `apps/desktop/src/notesWindowState.js` - `DEFAULT_SIZE = {420, 740}`, `MIN_NOTES_SIZE = {360, 480}`, new pure `compactBounds(current)` (same x/y/width, height = the composer band, ~132px) and `restoredBounds(saved)`
- Modify: `apps/web/src/pages/NotesPopout.tsx` - the two title-bar controls; `window.diarizNotes` typed in `lib/notesPopoutBridge.ts` (new, mirrors `trayRecorder.ts`'s `TrayBridge` pattern)
- Test: `apps/desktop/src/notesWindowState.test.js`, `apps/web/src/pages/NotesPopout.test.tsx`

- [ ] **On top** toggle (24px, `aria-pressed`, pin glyph): calls `setAlwaysOnTop(!onTop)`; state seeded `true` (the window is created `alwaysOnTop: true`). Hidden when `window.diarizNotes.setAlwaysOnTop` is absent (a browser tab opened at `/notes-popout` by hand).
- [ ] **Compact** (24x24, `aria-pressed`): renders only the composer band (status line + composer) and asks the shell to shrink; pressing again restores the stream and the previous size. Compact is **renderer state**; the shell only resizes. Saved bounds must never be the compact ones - `trackBounds` skips while compact and the restore writes the real size back.
- [ ] Tests (desktop, `node --test`): `compactBounds` keeps x/y/width; `notesWindowBounds` honours the new minimum; a saved height below the new minimum is raised. Tests (web): both controls hidden without the bridge; On top toggles `aria-pressed` and calls the bridge; Compact hides the stream, keeps the composer usable, and restores.

### Task 13: Three global hotkeys and where each one lands

**Files:**
- Modify: `apps/desktop/src/screenshotState.js` - generalise: `HOTKEY_ACTIONS = [{ key: "captureHotkey", default: "CommandOrControl+Shift+9", action: "capture" }, { key: "noteHotkey", default: "CommandOrControl+Shift+0", action: "focus-composer" }, { key: "transcriptChatHotkey", default: "CommandOrControl+Shift+8", action: "transcript-to-chat" }]`, `accelerators(store)`, and a pure `routeNotesCommand(action, { popoutOpen })` -> `"popout" | "main"` (`focus-composer` goes to the pop-out when it exists, else main; `transcript-to-chat` always main - the chat lives there). Pure `formatAccelerator(acc, platform)` -> "Ctrl+Shift+0" / "⌘⇧0" for the hint line.
- Modify: `apps/desktop/src/main.js` - `applyShortcut` registers all three under the same `canCapture(recorder)` gate (recording + renderer ready - so Diariz never holds a global key while idle, exactly as today); one "hotkey unavailable" notification per attempt covers whichever failed; delivery: `notesWindow.show(); focus(); webContents.send("notes:command", {type})` or `mainWindow.show(); focus(); webContents.send("notes:command", ...)`.
- Modify: `apps/desktop/src/preload.js` - `onNotesCommand(cb)`, `loadHotkeys()` -> the three formatted accelerators; `notes-preload.js` - `onNotesCommand(cb)` (Task 12)
- Modify: `apps/web/src/lib/trayRecorder.ts` (`TrayBridge` gains the two), `apps/web/src/components/Recorder.tsx`, `LiveNotesStream.tsx` (hotkey line + a `focusRequest` counter prop that focuses the input when it changes), `NotesPopout.tsx`
- Test: `apps/desktop/src/screenshotState.test.js`, `apps/web/src/components/Recorder.test.tsx`, `LiveNotesStream.test.tsx`, `NotesPopout.test.tsx`

- [ ] `Recorder`: on `focus-composer` - if popped out, ignore (the shell routed it to the pop-out); else `hub.toggle("notes")` if closed and bump `focusRequest`. On `transcript-to-chat` - the same handler the button uses (Task 10), and the popover's confirmation shows if it is open.
- [ ] `NotesPopout`: on `focus-composer` - leave compact mode alone (the composer is visible either way) and focus the input.
- [ ] **Hint line** (10px `--hub-placeholder`, bottom of band 5): "{note} note anywhere · {capture} capture · {chat} send transcript to chat", built from `loadHotkeys()`; hidden in a plain browser. The middle dot is fine; no dashes.
- [ ] Tests (desktop): routing table; defaults; a stored override wins; `formatAccelerator` both platforms. Tests (web): the line renders the accelerators the bridge reports, and nothing without the bridge; `focusRequest` focuses the input; `Recorder` opens the closed popover on the command and does nothing when popped out.

### Task 13b (optional, user decision): three rows in `hotkey.html`

- [ ] Extend the hotkey window (`hotkey.html`, `hotkey-preload.js`, `hotkey:load` / `hotkey:save` to take an action key) so all three accelerators are user-configurable. If skipped, the two new ones are still stored under their keys so this can land later without a migration of anything.

### Task 14: Docs and the release checklist (PR C)

- [ ] Version + mirrors + `RECENT[0]` (minor bump).
- [ ] `CAPABILITIES` **Notes** row (on-top toggle, compact, hotkeys); README/features.md lockstep.
- [ ] Help `recording-audio.md` - "Keeping notes visible during a call" (On top, Compact) and the hotkeys (name the defaults, say they only work while recording).
- [ ] `docs/Overall_Synopsis_of_Platform.md` IPC table (~line 1504): `notes:set-always-on-top`, `notes:set-compact`, `notes:command`, `hotkeys:load`; the hotkey paragraph (~1416/1443) now covers three accelerators under one gate.
- [ ] `apps/desktop/src/notesWindowState.js` default-size change is a desktop change: **PR body says "desktop release required" - cut it by pushing a `v*` tag after merge**, and it also needs the server redeploy for the web half. The Windows installer is built by CI; the macOS `.dmg` by hand (`npm run dist` on a Mac). Verify `⌘⇧` formatting on macOS while there.
- [ ] **Manual checks before opening the PR:** each hotkey fires with Teams/Zoom focused; the note hotkey raises the pop-out when it exists and the main window when it does not; compact -> restore returns to the same size and the saved bounds are the full ones; toggling On top off lets the window fall behind the call and back.

---

## Verification (every PR)

- [ ] `cd apps/web && npx vitest run` on Linux (Global Constraints) - count matches CI, zero `act` warnings.
- [ ] `cd apps/desktop && npm test` (PR C).
- [ ] `cd apps/web && npm run build` (tsc + vite) - the popover and pop-out are eager routes, so no new heavy import: `bundleBoundary.test.ts` stays green.
- [ ] Light and dark, both hosts: the transcript stamps are `--hub-muted` (AA), the note rail and hover backgrounds come from the CSS classes, no literal hex.
- [ ] A recording with **no live session** (stop the API's live endpoint or start offline): the panel shows notes + captures only, no Use in chat, capture Chat disabled with the "once saved" reason, attach-on-stop unchanged.
- [ ] A recording past one hour: stamps widen to 50px, nothing wraps.

## Self-review

Before opening each PR, check the diff against the handoff's **Interactions & behaviour** table row by row, and against this plan's Decisions section: every deviation from the handoff must be one listed there, and every listed one must be in the PR body so the reviewer is not left to find it.
