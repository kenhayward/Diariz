# Meeting screenshots (Windows desktop client)

**Status:** approved design, ready for implementation planning
**Date:** 2026-07-22

## Problem

While a meeting is being recorded, the useful thing on screen is often not in the audio: a shared slide,
a diagram, a spreadsheet, a chart someone talked over. Today the only way to keep it is an external
screenshot tool, and the image then has no relationship to the transcript - the user has to remember
which picture went with which part of the conversation.

This feature lets the user capture the screen during a recording from the desktop client, and have each
capture land in the transcript at the moment it was taken.

## User-facing behaviour

1. During a recording, the user captures a screenshot with a configurable global hotkey, a tray menu
   item, or a button in the app.
2. The **first** capture of a recording opens a picker: click a monitor to capture that whole screen, or
   drag a rectangle on it. That target is reused for every later capture in the same recording.
3. The target resets when a new recording starts. A "Change capture area" action re-opens the picker
   mid-recording.
4. Captures are stored against the recording and count toward the owner's storage quota.
5. After transcription, each capture appears in the transcript as its own row at its captured time,
   showing a thumbnail. Clicking the thumbnail opens a modal with the full image.
6. The Notes tab carries a "Screenshots (n)" section, collapsed by default, listing the recording's
   captures; clicking one opens the same modal.
7. While recording, the live notes popover shows a strip of the captures taken so far, so a mis-aimed
   capture area is discovered immediately rather than after the meeting.

Screenshots are a desktop-shell capability. In a plain browser (no `window.diariz`) the capture button
and related affordances are hidden; existing recordings simply have no screenshots.

## Feasibility

Confirmed against the shipped stack. Nothing here needs a new native dependency or a new external service.

**Electron 43 (already the pinned version):**

- `desktopCapturer.getSources({ types: ["screen"], thumbnailSize })` returns a full-resolution
  `NativeImage` of a monitor when `thumbnailSize` is the display size multiplied by its `scaleFactor`.
  Main-process only since Electron 17, which suits this design.
- `screen.getAllDisplays()` supplies the monitor list, bounds and scale factors for the picker.
- `nativeImage.crop()` and `nativeImage.resize()` produce the rectangle crop and the thumbnail in
  process, so the API needs no image library.
- `globalShortcut.register()` allows capture while the meeting app has focus, which is the normal case.
- Windows needs no additional permission. macOS reuses the Screen Recording grant the app already
  requires for system-audio capture, so the feature is not Windows-only by construction, though only
  Windows is in scope for this spec.

**Existing platform pieces this composes from:**

| Need | Existing precedent |
|---|---|
| Stamp a capture on the pause-aware recorded clock | `Recorder.tsx` `timingRef` / `MeetingNote.CapturedAtMs` |
| Survive a crash before the recording exists | `apps/web/src/lib/pendingNotes.ts` (IndexedDB stash) |
| Attach after the recording id exists, with retry | `Recorder.attachNotes` and its retry banner |
| Insert an item into the transcript at its time | `apps/web/src/lib/transcriptNotes.ts` (`weaveTranscript`) |
| Store a blob, quota it, serve it to the browser | `AttachmentsController` (`BlobKey`, `IStorageUsage`, `?access_token=`) |
| Drive an action from the tray | `tray:command` IPC and `reportRecorderState` |

The only genuinely new mechanism is the capture-target picker overlay.

## Architecture

The split follows the existing tray-recording pattern: **main owns the capture, the renderer owns the
clock.**

- **Electron main** owns hotkey registration, tray items, the picker overlay, the per-recording capture
  target, and the grab/crop/resize. It already tracks recording phase via `reportRecorderState`, so it
  knows when capture is legal.
- **Renderer (web `Recorder`)** stamps `capturedAtMs` using `timing.elapsedMs(timingRef.current, Date.now())`
  - the same call `addLiveNote` uses, so pauses are handled identically - stashes the bytes durably,
  uploads them once the recording row exists, and retries on failure.

Main never learns the recording clock; the renderer never learns the capture target. Each side is
independently testable.

### Capture flow (main)

```
hotkey / tray "Capture Screenshot" / renderer IPC request
  -> capture target set for this recording?
       no  -> open picker overlay, await selection (Escape cancels)
  -> desktopCapturer.getSources({ types: ["screen"], thumbnailSize: display.size * scaleFactor })
  -> nativeImage.crop(rect)              (whole-monitor target: no crop)
  -> full:  PNG,  long edge capped at MaxLongEdge (default 2560)
     thumb: JPEG, long edge ~320
  -> webContents.send("screenshot:captured", { full, thumb, width, height })
```

PNG for the full image because screenshots are text and slides, where JPEG artefacts destroy
legibility; JPEG for the thumbnail because it is small and never read closely.

### Picker overlay

A transparent, frameless, always-on-top `BrowserWindow` per display. Click a monitor to select the
whole screen; drag to select a rectangle on it. Escape cancels with no capture and no error state. The
selection is stored in main for the rest of the recording and cleared on every transition into
`recording`. "Change capture area" (tray item and a button in the live notes popover) clears it and
re-opens the picker.

### New pure modules (desktop)

Both unit-tested with `node --test`, in the style of `recorderState.js`:

- `apps/desktop/src/captureTarget.js` - `cropRectFor(display, selection)` (scale-factor aware),
  `clampRect(rect, bounds)`, `resizeDims(width, height, maxLongEdge)`.
- `apps/desktop/src/screenshotState.js` - tray item descriptors by recorder phase (capture items
  enabled only while recording), hotkey accelerator formatting and validation.

### Renderer stash and attach

New `apps/web/src/lib/pendingScreenshots.ts`, closely mirroring `pendingNotes.ts`, in its own IndexedDB
database (`diariz-screenshots`) so it does not force a version bump on an existing store. Records
`{ capturedAtMs, full: Blob, thumb: Blob, width, height }` keyed by user id, with `recordingId` null
while recording and set if the audio uploaded but the screenshot attach failed.

On successful audio upload the Recorder calls `attachScreenshots(recordingId)` alongside `attachNotes`.
Contract, identical to notes: a screenshot failure never fails the audio upload; the stash retains the
recording id; the retry path re-attaches.

This ordering is forced by the domain - the recording row does not exist until the audio upload
completes, so mid-meeting captures must be durable locally first.

## Data model

New entity `MeetingScreenshot`, mirroring `MeetingNote`:

| Column | Type | Notes |
|---|---|---|
| `Id` | guid | PK |
| `UserId` | guid | Owner; every endpoint filters on it |
| `RecordingId` | guid | FK, cascade delete |
| `CapturedAtMs` | long | Non-null; a screenshot is always a capture fact |
| `BlobKey` | text | `{userId}/screenshots/{id}.png` |
| `ThumbBlobKey` | text | `{userId}/screenshots/{id}.thumb.jpg` |
| `Width`, `Height` | int | Full-image pixel dimensions |
| `SizeBytes` | long | Full plus thumbnail; counted by `IStorageUsage` |
| `Ordinal` | int | Sort order within the recording |
| `CreatedAt` | timestamptz | Stored as UTC |

Index on `(RecordingId, CapturedAtMs)`.

A separate table rather than a new `Attachment` kind: screenshots would otherwise appear in the Files
tab and every existing attachment consumer (chat context, folder attachments, minutes, email
attachments) would have to learn to exclude them. `MeetingNote` already proved this shape, and the
transcript weaver already speaks it.

### Storage lifecycle

- Recording delete removes both blobs explicitly before removing rows, matching the attachment rule
  that a dangling row is safer and more retriable than an orphaned blob.
- Audio-retention deletion (`AudioDeleted`) leaves screenshots intact: the transcript survives, so its
  images should too.
- Backup and restore gain the table and its blobs. The change is purely additive, so
  `MaintenanceController.CurrentFormat` does **not** need bumping - an older backup restores with no
  screenshots, which is correct.

## API

`ScreenshotsController` at `/api/recordings/{recordingId}/screenshots`:

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/` | Multipart: full image, thumbnail, `capturedAtMs`, `width`, `height` |
| `GET` | `/` | List for the recording |
| `GET` | `/{id}/content` | Full image bytes |
| `GET` | `/{id}/thumb` | Thumbnail bytes |
| `DELETE` | `/{id}` | Remove row and both blobs |

Ownership is checked on every route from the JWT `NameIdentifier` claim. Size is capped by a new
`ScreenshotOptions.MaxBytes`, and the owner's storage quota is enforced before writing, exactly as
`AttachmentsController.AddFile` does.

Both content routes accept the bearer token via the `?access_token=` query string, the same mechanism
`attachmentContentUrl` uses. This is what makes a plain `<img src>` work, since the web client sends
its token through axios headers that an image request cannot carry.

## Transcript insertion

`weaveTranscript` currently interleaves segments with notes. It is refactored to weave a single list of
tagged timed items (`{ kind: "note" | "screenshot", capturedAtMs }`), leaving `anchorIndex` untouched
and updating its existing tests. This is a small, contained change to code the feature has to touch
anyway.

**Server-side merge break.** `RecordingsController` feeds note capture times into
`TranscriptNoteAnchor.BreakBeforeIndices` so a same-speaker segment merge cannot swallow the boundary a
note sits on. Screenshot capture times must join that same union. Without this, a screenshot taken
between two same-speaker segments that later merge would jump to the end of the merged block - an
intermittent "my screenshot is in the wrong place" defect with no visible cause. This is the single
most easily missed correctness requirement in the feature.

## UI

- **Transcript** - a woven `ScreenshotRow`: timestamp badge, inline thumbnail, click opens the modal.
- **`ScreenshotModal`** - full image, previous/next through the recording's captures, jump to that
  moment in playback, download, delete.
- **Notes tab** - a "Screenshots (n)" section, collapsed by default, below the note list; thumbnails
  open the same modal.
- **Live notes popover** - a strip of the captures taken so far plus a "Change capture area" button.
  This is the feedback loop that makes the define-once-reuse model safe.
- All screenshot affordances are hidden without `window.diariz`.

All user-facing strings go through the i18n catalogs, with plain hyphens rather than em or en dashes.

## Hotkey configuration

Stored in `electron-store` next to the server address, not in server `UserSettings`: the right hotkey
depends on the machine's keyboard and on what other software has claimed, so it is a machine-local
concern, and the desktop shell already owns exactly this class of setting.

A small hotkey window is reachable from the tray. It validates the accelerator and reports a failed
`globalShortcut.register` (the combination is taken) rather than silently doing nothing. The shortcut
is registered only while a recording is active and released on stop, so Diariz never holds a global key
while idle.

## Errors and edge cases

| Situation | Behaviour |
|---|---|
| Picker cancelled with Escape | No capture, no error state, target stays unset |
| Display unplugged between captures | Target invalid: re-prompt rather than grab the wrong monitor |
| Capture while recording is paused | Allowed; stamped at the paused clock position |
| Storage quota exceeded on attach | Stash retained and surfaced for retry, as with notes |
| Recording stopped between grab and attach | Bytes stay stashed; the retry path attaches them |
| `globalShortcut.register` fails | Reported in the hotkey window; tray and in-app capture still work |
| Capture requested while not recording | Ignored; tray items disabled in that phase |

## Testing

Test-first throughout, per the project's TDD rule.

- **Desktop** (`node --test`, no Electron): `captureTarget.js` crop maths, scale factors, clamping and
  resize dimensions; `screenshotState.js` tray items by phase and hotkey validation.
- **Web** (vitest): `pendingScreenshots` round-trip; `weaveTranscript` with mixed notes and screenshots;
  RTL component tests for the transcript row, the modal, and the collapsed Notes section.
- **API unit** (`Diariz.Api.Tests`, in-memory plus `FakeAudioStorage`): ownership, quota rejection,
  size cap, blob cleanup on delete.
- **API integration** (`Diariz.Api.IntegrationTests`, Testcontainers): real MinIO blob round-trip, FK
  cascade, and the merge-break union - the in-memory provider will not translate that query faithfully,
  so it belongs at the integration layer.

## Ship surface

The change touches `apps/desktop/src/**`, so shipping it needs a **desktop release** (a `v*` tag) in
addition to a server redeploy.

Release checklist items 1 to 7 all apply: version bump plus its three mirrors, a `RELEASES[0]` entry,
the About-box `CAPABILITIES` row, the README Features row, the `docs/features.md` bullet,
`docs/Overall_Synopsis_of_Platform.md` (new IPC contract, new endpoints, new desktop capability), and
`docs/Data_Schema.md` (new table, new MinIO key layout).

## Out of scope (future development)

Recorded deliberately, not overlooked:

- **OCR / text extraction** from captures, so slide text becomes searchable and citable.
- **Feeding images to the summariser or minutes**, which would need a vision-capable model and a
  per-user configuration story.
- **Annotation and markup** on a capture (arrows, highlights, redaction before storage).
- **Browser-only capture** without the desktop shell, via `getDisplayMedia` plus a canvas grab.
- **Window-target capture** as a third target kind, following a specific window as it moves. Deferred
  because a window can be closed or minimised mid-recording and some GPU-composited windows capture
  black on Windows, both of which need their own fallback behaviour.
