# Automatic Slide Capture - Feature Specification

**Status:** specified, ready to implement (all open questions decided - see §12)
**Surface:** Electron desktop shell (`apps/desktop`) + web recorder (`apps/web`)
**Server changes:** none

---

## 1. What the user asked for

A third control in the notes popover, alongside **Capture screenshot** and **Change capture area**:
a sticky toggle that, while engaged, samples the chosen capture area once per second, discards
frames that are visually the same as the last kept one, and pushes only the genuinely-changed
frames into the recording as screenshots. The intent is to automate what a presenter's audience
currently does by hand: hit the hotkey on every slide.

The supplied brief (`slide-extraction-spec.md`) is a good description of the *detection algorithm*
and a poor fit for *this* product's architecture. Section 2 separates the two.

A third control makes the row too wide for text labels, so this change also **converts all three capture
controls to icon buttons** with the longer descriptions moved to hover text (§5.1).

---

## 2. Review of the supplied brief

### 2.1 What transfers, essentially unchanged

| Brief section | Verdict |
|---|---|
| §6 detector state machine (candidate + stability streak + commit) | **Adopt.** This is the core value of the brief and it is already causal/single-pass, so it works on a live stream with no changes to its logic. |
| §6 "the stability window is the critical part" | **Adopt, and do not optimise away.** Without it the feature captures mid-transition and half-rendered animation frames, which is worse than useless: it fills the transcript with junk the user then has to delete one by one. |
| dHash over raw pixel differencing | **Adopt.** Cursor movement, codec noise, and a blinking caret are exactly the failure modes, and structural hashing is what survives them. |
| §5 / §7 sample-small, extract-large split | **Adopt the principle.** Detect on a small grayscale frame; grab full resolution only for frames that commit. The mechanism differs completely (see 2.2). |
| §8 non-adjacent dedupe ("let me go back two slides") | **Adopt in an online form.** Hash-compare each commit against every previously committed hash in the session, not just the last one. |
| §11 typed errors, logging, cancellation | **Adopt in spirit.** Maps onto the shell's existing `notificationForCaptureFailure` / in-flight-guard conventions rather than onto Python exceptions. |
| §12 "assert exact expected counts against synthetic fixtures" | **Adopt.** The fixtures are synthetic bitmaps, not MP4s (see 2.2). |

### 2.2 What does not transfer, and why

**The brief assumes a recorded video file exists. In Diariz, one never does.**

Diariz records **audio only**. There is no screen recording anywhere in the platform - the desktop
shell grabs still frames on demand via `desktopCapturer` (`apps/desktop/src/main.js` `grab()`), and
the Python worker only ever sees an audio blob. So:

- **§4 `slides/` Python package, §5 `sampler.py`, §7 `extractor.py`** - there is no video to sample
  or seek into. The frame source is `desktopCapturer.getSources()` in the **Electron main process**,
  on a live 1 Hz ticker. Everything in the brief about `ffmpeg` piping, raw-frame stdout, `ffprobe`
  duration probing, seek nudging, and orphaned-subprocess cleanup is not applicable.
- **§3 dependency constraints (`opencv-python-headless`, `imagehash`, "must stay torch-free")** - moot.
  This code lands in JavaScript in the Electron main process. It should also add **no npm dependency**:
  dHash over a 17x16 grayscale bitmap is about 30 lines, and `nativeImage.resize()` already does the
  downsampling. The desktop shell is deliberately dependency-light and this does not earn an exception.
- **§7 concurrency (`ThreadPoolExecutor`, 4 workers)** - there is exactly one grab at a time, gated by
  the existing `captureInFlight` flag. Nothing to parallelise.
- **§9 `SlideSet` / `Slide` output contract, JSON serialisation, content-addressed cache** - the
  output contract already exists and is `MeetingScreenshot` (`src/Diariz.Domain/Entities/MeetingScreenshot.cs`)
  plus the `screenshot:captured` IPC payload. Introducing a second, parallel representation would be
  a regression. Caching is meaningless for a live stream that is never replayed.
- **§6 masking / `mask_regions`** - **subsumed by an existing feature.** The brief needs masking because
  a presenter webcam overlay never stabilises and suppresses every slide behind it. Diariz already makes
  the user choose a **capture area** (a screen or a rectangle) before any capture, and auto-capture will
  reuse that same area. A user whose deck has a webcam tile beside it crops it out with the picker. Adding
  a second, normalised-rectangle masking config on top of the rectangle picker is redundant UI for a case
  the picker already covers. Revisit only if real recordings show an overlay *inside* an unavoidable crop.
- **§6 `motion_run_limit` / `motion_spans` metadata** - **drop.** The stability window already suppresses
  commits during embedded video or scrolling: nothing stabilises, so nothing commits. The separate motion
  bookkeeping exists in the brief only to emit `motion_spans` for a downstream consumer (§13), and Diariz
  has no such consumer. Adding an unused output is carrying cost for nothing. Detection of "we are in
  motion" is worth keeping only as a *debug log line*, not as a data product.
- **§12 ffmpeg fixture-video generator, §12 calibration CLI over a labelled recording, §14 acceptance
  criteria 1/2/4/5** - all rooted in offline video processing, slim containers, and pip resolution. Replaced
  by the criteria in §10 below.
- **§13 downstream vision-LLM OCR + diarized-turn alignment** - explicitly out of scope, and note that
  Diariz already places screenshots inline in the transcript at their capture offset, which is most of what
  §13's alignment was reaching for.

### 2.3 Where the brief is actively wrong for a live stream

1. **The commit timestamp.** The brief commits a slide, then seeks to `timestamp + stable_samples/fps`
   to land inside the settled region, so its recorded start time drifts late by the stability window. Live,
   we hold both facts at once: the **candidate's first-seen offset** (when the slide actually appeared) and
   the **settled image** (grabbed at commit). We record the former as `capturedAtMs` and use the latter as
   the image. This is strictly better than the brief's compromise and costs almost nothing (§4.5).
2. **Sample rate vs. stability window.** The brief's defaults (2 fps, `stable_samples=3`) mean 1.5 s of
   stability. At 1 Hz, `stable_samples=3` becomes a 3 s dwell requirement - and any slide shown for under
   3 s is **missed entirely**, silently. This spec originally lowered the default to 2 to compensate;
   B1's tests then showed 2 admits mid-transition frames, so the stability window stays at **3** and the
   sample interval is what has to come down instead. See §6 and §14.
3. **Commit-time races.** The brief's content is static by construction because it re-reads a file. Live, the
   screen can change between the sampling tick that triggered the commit and the full-resolution grab that
   follows it. That needs an explicit confirm step (§4.4), which the brief has no reason to describe.

---

## 3. Naming

**Decided: `Auto-capture`.** "Capture Changes" reads like a diff/version-control action. The alternatives
considered and rejected:

| Label | Why not |
|---|---|
| Auto-capture slides | Clearer intent, but wrong whenever the capture area is a whiteboard, a shared document, or a code demo - all of which this handles equally well. |
| Watch for changes | Accurate, but describes the monitoring rather than the outcome. Users would ask "watch and then what?". |
| Slide capture | Reads like a mode, not a toggle, and inherits the same slide-specific narrowness. |

The three controls become **icon buttons** (§5.1), so `Auto-capture` is the accessible name and the hover
text carries the meaning. In the release notes and help article, lead with the use case: "capture every
slide in a presentation without touching the keyboard".

i18n keys in `apps/web/src/locales/*/workspace.json`. Two of the three existing labels stay as accessible
names and gain a longer hover description; auto-capture is new:

```
// existing, now used as aria-label rather than as visible button text
"screenshotCaptureButton": "Capture screenshot",
"screenshotCaptureArea": "Change capture area",

// new hover descriptions (title attribute)
"screenshotCaptureButtonHint": "Take one screenshot of the capture area now",
"screenshotCaptureAreaHint": "Choose which screen or which part of it is captured",

// new, auto-capture
"screenshotAutoCapture": "Auto-capture",
"screenshotAutoCaptureOn": "Auto-capture is on",
"screenshotAutoCaptureHint": "Capture the screen automatically each time it changes",
"screenshotAutoCaptureOnHint": "Auto-capture is on - click to stop capturing automatically",
"screenshotAutoCaptureStopped": "Auto-capture stopped - this recording reached its limit of {{max}} screenshots."
```

`screenshotCaptureNeedsArea` ("Set a capture area first") already exists and becomes the hover text for
both capture buttons while no area is set. No em dashes or en dashes in any of the above (project
convention).

---

## 4. Design

### 4.1 Where the work lives, and why

**All detection runs in the Electron main process.** The alternative - shipping a full-resolution PNG to
the renderer every second and hashing it there - would push roughly 1 to 3 MB per second across
structured-clone IPC and decode it in the renderer, for frames that are 95% destined for the bin. Detection
at the source means **only kept frames ever cross IPC**, on the existing `screenshot:captured` channel with
the existing payload shape.

That is the load-bearing property of this design: **nothing downstream of the IPC boundary changes.**
`trayScreenshots.ts`, `Recorder.addLiveShot`, `pendingScreenshots` (IndexedDB stash), the attach-on-stop
upload loop, `POST /api/recordings/{id}/screenshots`, the quota accounting, `ShotStrip`, and the inline
transcript rendering all keep working untouched, because an auto-capture is indistinguishable from a
hotkey capture by the time it reaches them.

### 4.2 New pure module: `apps/desktop/src/slideDetector.js`

Follows the shell's established pattern (`recorderState.js`, `screenshotState.js`, `captureTarget.js`,
`pickerPool.js`, `updateState.js`): all logic pure and unit-tested with `node --test`; `main.js` owns the
Electron objects and the timer.

```js
/// Perceptual hash of one grayscale sample. `bitmap` is BGRA bytes from
/// nativeImage.getBitmap() at exactly (HASH_SIZE + 1) x HASH_SIZE.
/// Returns a Uint8Array(32) - a 256-bit dHash (HASH_SIZE = 16, matching the brief).
function dhash(bitmap, width, height)

/// Popcount of the XOR. 0 = identical, 256 = maximally different.
function hamming(a, b)

/// The state machine. Construct per auto-capture session; feed it one hash per tick.
/// Returns null (nothing to do) or a commit descriptor.
function createDetector(config)
  detector.observe(hash, atMs)      // -> null | { firstSeenAtMs, hash }
  detector.confirm(hash)            // -> boolean; commit-time race guard (4.4)
  detector.reject()                 // full-res grab failed/mismatched: drop the candidate
  detector.isDuplicate(hash)        // -> boolean; online back-navigation dedupe (4.5)
```

### 4.3 The tick

`main.js` runs a `setInterval` at `sampleIntervalMs` (default 1000) that exists **exactly while
auto-capture is engaged**, and is torn down on disengage, on recording end, and on app quit.

Each tick:

1. Bail if `!canCapture(recorder)` (the existing predicate: recording, renderer ready) or the recording
   is paused (§7.2), or `captureInFlight` is set by a manual capture. **Skip the tick, never queue it** -
   a queued backlog would fire a burst of grabs the moment a manual capture finished.
2. Grab the capture area at **detection resolution**, not full resolution: `desktopCapturer.getSources()`
   with `thumbnailSize` sized so the display's long edge is `detectLongEdge` (default 320). This is the
   single most important cost decision in the feature - a full 4K composite once per second for an hour is
   3600 full-resolution grabs and encodes, and is not acceptable.
3. Crop to the selection. The existing `cropRectFor()` computes a crop in *physical pixels of a
   full-resolution grab*, so it cannot be reused as-is. Add a pure sibling in `captureTarget.js`:
   `cropRectForSize(display, selection, imageSize)`, which scales the selection to whatever size the
   grab actually came back at. (`thumbnailSize` is a request, not a guarantee - the existing code already
   carries a comment about this for the fractional-Windows-scaling case.)
4. Resize the crop to 17x16, read `getBitmap()`, compute the dHash, hand it to `detector.observe()`.

### 4.4 Commit, and the commit-time race

When `observe()` returns a commit:

1. Take `captureInFlight` (so a manual capture cannot interleave).
2. Run the **existing `grab(captureTarget)`** unchanged - full resolution, capped at `MAX_LONG_EDGE`,
   plus the JPEG thumbnail. Same code path as a hotkey capture, therefore same output characteristics.
3. **Confirm.** Re-hash the full-resolution image and check it still matches the candidate hash within
   `stabilityThreshold`. The screen can change in the ~100 to 300 ms between the deciding tick and this
   grab; without the confirm we would silently file the *next* slide's image under *this* slide's
   timestamp, which is worse than missing the slide. On mismatch: `detector.reject()`, discard, let the
   next ticks re-detect. Log at debug.

   **The two hashes must be computed through the same downsample chain**, or they are not comparable:
   a 320 px sample resized to 17x16 and a 2560 px image resized to 17x16 do not produce the same hash.
   So the confirm path resizes the full-resolution image to `detectLongEdge` **first**, then to 17x16.
   This is a real trap; make it a named helper used by both paths so it cannot drift.
4. `detector.isDuplicate()` check (§4.6). If duplicate, drop and log.
5. Send `screenshot:captured` with `{ full, thumb, width, height, ageMs }` (§4.5) and record the commit
   hash.
6. Release `captureInFlight`, set `lastCaptureAt`.

### 4.5 Timestamping: the `ageMs` hint

**Decided: implemented.** Without it every auto-captured slide is filed `stableSamples` seconds late (2 s
at the defaults), which is enough to place a slide *after* the sentence that introduced it - the sentence
a reader is most likely scanning for when they want that slide.

The renderer owns the recorded clock (pause-aware) and stamps captures at receipt. The shell knows when the
slide actually appeared but has no access to that clock. Rather than trying to align two clocks across a
process boundary, the shell sends an **age**, not a timestamp:

- The payload gains `ageMs?: number` - milliseconds between the candidate's first-seen sample and the
  moment the payload is sent, measured entirely inside the main process.
- The renderer computes `capturedAtMs = max(0, recordedNowMs - ageMs)`.

This needs no shared epoch, no clock-skew handling, and no monotonic-clock plumbing. It is also robust to
the send itself being slow: `ageMs` is computed at send time, not at commit time.

The field is **optional**, so an older shell paired with a newer web app keeps today's stamp-at-receipt
behaviour rather than breaking. Manual captures omit it entirely - there is no candidate window to
subtract, and the receipt moment *is* the capture moment.

Pause safety: a pause during the candidate window would make `ageMs` span un-recorded wall time and land
the capture too early. §7.2 already clears the in-flight candidate on pause, so no `ageMs` can ever span
one. The `max(0, ...)` clamp is a belt-and-braces guard, not the mechanism.

This is the one part of the design that touches the renderer (`trayScreenshots.ts` and
`Recorder.addLiveShot`). Everything else downstream of IPC is untouched.

### 4.6 Online dedupe (back-navigation)

**Decided: suppress duplicates.** Keep every committed hash for the session in an array. Before sending,
compare the new commit against all of them at `dedupeThreshold`; on a hit, **suppress the capture entirely**
and log which earlier commit it matched.

This is a product decision, not just a storage saving: a deck where the presenter flips back and forth
would otherwise produce five copies of the same slide interleaved through the transcript, and the user
would delete them by hand - the exact chore the feature exists to remove.

The cost is that "we returned to slide 3 at 24:10" is not recorded anywhere. The brief models this with
`Slide.occurrences`, and `MeetingScreenshot` has no equivalent. **Out of scope for v1**; the natural
extension is an `Occurrences` JSON column on `MeetingScreenshot` plus a marker in the transcript, and it
should not be built until someone asks for it.

The array is bounded by `maxCaptures` (§6), so the comparison is at most a few hundred 32-byte XORs per
commit. Not worth optimising.

### 4.7 Interaction with the existing capture path

| Existing behaviour | Interaction |
|---|---|
| `canCapture(state)` gate (recording + renderer ready) | Auto-capture is gated on the same predicate. Never a second, divergent gate. |
| Capture area required first | Auto-capture cannot be engaged until an area is set, exactly as the manual capture button is disabled (`captureAreaSet`). Engaging it should **not** open the picker. |
| `captureInFlight` / `CAPTURE_COOLDOWN_MS` | Auto-capture's commit path takes `captureInFlight`. Detection ticks *skip* rather than queue when it is held. The 750 ms cooldown is a hotkey-auto-repeat guard and does not need to gate a 1 Hz ticker, but sharing `lastCaptureAt` costs nothing and keeps one source of truth. |
| Hotkey / tray / popover manual capture | Remain fully available while auto-capture is engaged. A manual capture **also updates the last committed hash**, so the user manually grabbing the current slide does not cause the detector to grab it again a second later. |
| `MAX_LIVE_SCREENSHOTS = 200` | See §7.3. |
| Change capture area mid-recording | Resets the detector completely (clear committed hashes and candidate). The old area's hashes are meaningless against a new crop. |

---

## 5. UX

### 5.1 Notes popover (`apps/web/src/components/hub/NotesPopover.tsx`)

**All three controls become icon buttons.** Three text buttons do not fit the row: the popover is 400 px
wide (`HubPopover width={400}`, 18 px padding each side) and already spends the left of the row on the
`Screenshots (n)` count. "Auto-capture" + "Capture screenshot" + "Change capture area" is roughly 300 px of
text at 12 px plus padding and borders, which wraps or overflows.

```
Screenshots (7)                              [ ⬚ ]  [ ⧉ ]  [ ⌗ ]
                                          auto   shot   area
```

Order stays left to right as **Auto-capture, Capture screenshot, Change capture area** - the new control
sits leftmost so the two existing buttons keep their current relative positions and muscle memory.

**Icons.** Inline SVG components in the same house style as the file's existing `IconPopOut`/`IconClose`:
`viewBox="0 0 24 24"`, 16x16, `fill="none"`, `stroke="currentColor"`, `strokeWidth={2}`, round caps and
joins, `aria-hidden` + `focusable="false"`.

| Control | Glyph | Why |
|---|---|---|
| Auto-capture | Two offset rounded rectangles (a stack of frames) with a small filled dot at the top right | Reads as "many captures over time". Deliberately **not** a camera-with-a-loop: at 16 px that is indistinguishable from the camera next to it. The dot doubles as the live indicator (below). |
| Capture screenshot | Camera: rounded-rect body, small lens circle, shutter bump on top | The one universally-read glyph in the set. |
| Change capture area | Crop marks: two overlapping right angles | No circle and no filled mass, so its silhouette is unmistakable against the camera at 16 px. |

Button chrome is unchanged from today's text buttons (6 px radius, `1px solid var(--hub-border)`,
transparent background, hover to `var(--hub-surface-hover)`), resized to a 28x28 square - matching the
pop-out and close buttons already in this popover's header.

**Hover text is now the only label, so it has to work.** Each button carries `aria-label` (the short name)
and `title` (the longer description) from §3:

| Button | `aria-label` | `title` |
|---|---|---|
| Auto-capture, off | `screenshotAutoCapture` | `screenshotAutoCaptureHint` |
| Auto-capture, on | `screenshotAutoCaptureOn` | `screenshotAutoCaptureOnHint` |
| Capture screenshot | `screenshotCaptureButton` | `screenshotCaptureButtonHint` |
| Change capture area | `screenshotCaptureArea` | `screenshotCaptureAreaHint` |
| Either capture button, no area set | unchanged | `screenshotCaptureNeedsArea` |

**Gotcha: `title` does not render on a `disabled` button.** Chromium does not dispatch mouse events to
disabled form controls, so no tooltip appears. Today's code sets exactly that combination -
`disabled={!captureAreaSet} title={captureAreaSet ? undefined : t("screenshotCaptureNeedsArea")}` in
[NotesPopover.tsx](apps/web/src/components/hub/NotesPopover.tsx) - so the "Set a capture area first"
explanation is already invisible. With a text label that was a small loss; with an icon-only button the
user is left with a greyed glyph and no way to find out why. Fix it in this PR: use
`aria-disabled="true"` plus an early-return click handler instead of the `disabled` attribute, keeping the
button focusable and hoverable. Both capture buttons take the same treatment.

**Active state.** When auto-capture is engaged: `aria-pressed="true"`, filled background
(`var(--hub-surface-hover)`), border in `var(--hub-red)`, and the icon's corner dot animated with the same
`blink 1.2s infinite` the recording indicator in this popover's header already uses. Since the icon carries
no text, the state must be legible at a glance - the popover is dismissible, so §5.4 covers the rest.

**Props.** Following the file's existing optional-prop convention (absent in a plain browser, which is what
hides the whole section): `autoCapture?: boolean; onToggleAutoCapture?: () => void;`.

### 5.2 Pop-out notes window (`apps/web/src/pages/NotesPopout.tsx`)

**In scope, and easy to miss.** The detached notes window renders its **own copy** of this button row
(same labels, same `smallButton` style, same `screenshots (n)` count, its own `ShotStrip`), relaying clicks
to the host over `notesChannel` rather than calling the shell directly. It gets the same treatment:

- The same three icon buttons, in the same order, with the same `aria-label`/`title` pairs.
- The same `aria-disabled` fix - it carries an identical `disabled` + `title` pair on its capture button,
  so the "Set a capture area first" text is invisible there too. It additionally disables both buttons when
  the channel is dead (`!live`), which must also become `aria-disabled` so the window can still explain
  itself when the host has gone.
- `notesChannel` needs the auto-capture state and command added alongside the existing ones: `autoCapture:
  boolean` on the state payload (beside `canCapture` / `captureAreaSet`) and a
  `{ type: "toggle-auto-capture" }` message beside `{ type: "capture" }`, wired through
  `onToggleAutoCapture` in the handlers and `toggleAutoCapture()` on the client.

Because the two rows are now identical in structure and duplicated in full, **extract them into one shared
component** (`apps/web/src/components/hub/CaptureControls.tsx`) taking `{ autoCapture, captureAreaSet,
disabled, onToggleAutoCapture, onCapture, onChangeArea }`. Three icon buttons with paired
aria-labels/titles and a non-obvious disabled treatment is precisely the kind of thing that drifts between
two copies, and the copies have already drifted once (the popover gates the capture button on
`captureAreaSet`, the pop-out gates it on `!live || !captureAreaSet`). One component, one test file.

### 5.3 Tray menu (`apps/desktop/src/screenshotState.js`)

`trayScreenshotItems(state)` gains a third descriptor, a checkbox item:

```js
{ id: "auto-capture", label: "Auto-capture", type: "checkbox", checked: state.autoCapture === true,
  enabled: state.captureAreaSet === true }
```

Pure, so it is unit-tested with the rest of that module. `main.js` maps `auto-capture` to the toggle.

### 5.4 Visibility outside the popover

Auto-capture running unnoticed is the main way this feature annoys people (it is capturing their screen
once a second). Three cheap signals:

1. Tray tooltip while engaged: `Diariz - recording microphone, auto-capturing`.
2. A native notification on engage: "Auto-capture on - screenshots will be taken when the screen changes."
   No notification per capture (that would be intolerable at 1 Hz); the shot count in the popover and the
   live strip are the per-capture feedback.
3. Auto-capture **always disengages when the recording ends**, and never persists across recordings -
   consistent with the capture area, which each recording chooses afresh. Do not remember it in
   `electron-store`.

### 5.5 No new hotkey in v1

The shell already has one configurable global accelerator and a whole hotkey-capture window behind it.
A second one is disproportionate for a toggle the user flips once per meeting. Revisit if asked.

---

## 6. Configuration

Defaults live as constants in `slideDetector.js`. **Not user-facing in v1** - the brief is right that these
need calibration against real recordings, and shipping unvalidated sliders invites users to break the
feature and report it as broken. Make them overridable from `main.js` so a calibration harness can sweep
them, and promote them to settings only if real use shows one profile cannot cover Teams screen-share and
native 1080p capture.

| Setting | Default | Rationale |
|---|---|---|
| `sampleIntervalMs` | `1000` | **Decided: 1 Hz.** Halving it doubles the CPU cost of the ticker for a shorter detection latency. |
| `detectLongEdge` | `320` | Matches the brief's 320x180 sampling. Large enough that dHash is stable, small enough that the grab is cheap. |
| `hashSize` | `16` | The brief's value - a 256-bit dHash. |
| `changeThreshold` | `24` | Brief's value, at the same hash size. **Calibrate.** |
| `stabilityThreshold` | `12` | Brief's value. **Calibrate.** |
| `stableSamples` | `3` | **Was 2; corrected by B1's tests, which showed 2 admits mid-transition frames exactly as §6 warned it might.** A cross-fade does not drift smoothly through the hash space - dHash records the *sign* of each horizontal comparison and those signs flip together around the midpoint, so 25% and 50% through a fade produce the *same* digest. Two samples of one intermediate is all a streak of 2 needs, and a half-drawn frame commits. The cost is dwell time (a slide must hold for `stableSamples` x the sample interval), which is an argument for sampling faster than 1 Hz, not for lowering this. Both behaviours are pinned in `slideDetector.test.js`. |
| `dedupeThreshold` | `10` | Brief's value. Stricter than `changeThreshold` on purpose: a false dedupe silently loses a slide. |
| `maxCaptures` | `200` | Mirrors `MAX_LIVE_SCREENSHOTS`. See §7.3. |

---

## 7. Edge cases and failure modes

### 7.1 Nothing ever stabilises (embedded video, scrolling, a live demo)

Handled implicitly: no commit, no capture, no error. Log at debug when a run of N consecutive
non-stabilising samples is seen, purely so threshold tuning has a trail. Do **not** surface it to the user
and do **not** emit motion spans (§2.2).

### 7.2 The recording is paused

Auto-capture **must** suspend while paused - continuing would file screenshots at recorded offsets that
never advance, stacking every capture on one transcript position.

The shell's current phase model has no paused state: `RecorderState.phase` is
`"idle" | "recording" | "uploading" | "error"` (`apps/web/src/lib/trayRecorder.ts`), and `Recorder`'s
`recording` flag stays true through a pause. **This needs a cross-boundary change**: add
`paused?: boolean` to `RecorderState` and report it. Prefer a flag over a fifth phase - `recorderState.js`'s
menu/tooltip/notification switches all key on `phase`, and adding a case to each is more churn and more
risk than one orthogonal boolean.

Resume behaviour: keep the last committed hash across the pause, so a resume onto the same slide does not
re-capture it. Clear any in-flight candidate.

### 7.3 The screenshot cap is reached

`Recorder.addLiveShot` currently shows `screenshotLimitReached` and drops the capture. Under auto-capture
that would fire once per changed slide for the rest of the meeting, and the user would keep believing
captures were being taken.

**Auto-capture must self-disable at the cap.** The shell counts its own commits for the session and stops
at `maxCaptures`, disengaging the toggle, raising a notification, and showing
`screenshotAutoCaptureStopped` in the popover. Manual capture is unaffected (it retains today's behaviour).

Note the shell's count and the renderer's count can diverge (the user deletes captures from the live strip;
manual captures are counted too). The shell should count **all** captures it sends, manual and automatic,
and treat the cap as a ceiling rather than a precise balance. Getting this exactly right is not worth a
renderer-to-shell count sync; being conservative is.

### 7.4 Grab failures

Reuse the existing paths verbatim: a null `grab()` means the display went away or the crop degenerated, so
`setCaptureTarget(null)` and `notifyCaptureFailed("unavailable")`. Additionally, **disengage auto-capture** -
without an area it cannot run, and leaving the toggle lit while it silently does nothing is the worst
outcome. A thrown `desktopCapturer` error (permission revoked, macOS Screen Recording revoked, compositor
hiccup) does the same via `notifyCaptureFailed("error")`.

Transient single-tick failures should **not** disengage: only clear the candidate and continue. Distinguish
"the target is gone" (disengage) from "this one grab threw" (continue, and disengage after 3 consecutive
failures).

### 7.5 Display topology changes

`main.js` already reacts to display add/remove for the picker pool. Auto-capture rides on `captureTarget`,
which is cleared when its display disappears, so 7.4 covers it.

### 7.6 Renderer reload mid-recording

`canCapture` already requires `ready`. The ticker checks it every tick and skips, resuming automatically
when the renderer reports ready again. Per the existing note in
`apps/desktop/src/rendererReadiness.js`, a loading state is not navigation - do not tear the ticker down on
`did-start-loading`.

---

## 8. Testing (TDD, per CLAUDE.md)

Failing test first, in every case.

### 8.1 `apps/desktop/src/slideDetector.test.js` (`node --test`, pure)

Fixtures are **synthetic bitmaps generated in the test**, not video files: a small helper that renders
grayscale rectangles/blocks into a `(hashSize+1) x hashSize`-shaped buffer. This replaces the brief's §12
ffmpeg fixture generator entirely, and it is faster, hermetic, and diff-reviewable.

Scenarios, each asserting an **exact** commit count and the committed offsets:

| Scenario | Expected |
|---|---|
| Static frame for 30 samples | 1 commit (the first slide) |
| Static frame with a few pixels flipped each sample (cursor) | 1 commit |
| Clean cut to a new frame, then static | 2 commits; second at the first-seen offset, not the commit offset |
| Change that lasts 1 sample then reverts (transient overlay) | 1 commit total - the candidate is cleared, per brief §6 rule 2 |
| Change that ramps over 4 samples then settles (fade/animation) | 1 additional commit, at the settled content, not mid-fade |
| Every sample different for 20 samples (embedded video) | 1 commit total (the pre-motion frame); nothing during motion |
| Frame A, frame B, frame A again (back-navigation) | 2 commits; the third is reported duplicate |
| `confirm()` returns false | no commit; detector recovers and commits on the following stable run |

Plus unit tests for `dhash` (known bitmap to known bits; identical inputs to distance 0; inverted input to
a large distance) and `hamming` (symmetry, zero, saturation).

### 8.2 `apps/desktop/src/screenshotState.test.js` (extend)

`trayScreenshotItems` gains the auto-capture checkbox: present and unchecked when idle-but-capturable,
checked when engaged, absent when `canCapture` is false, disabled when no capture area.

### 8.3 `apps/desktop/src/captureTarget.test.js` (extend)

`cropRectForSize` against a grab that came back at exactly the requested size, larger, smaller, and at a
fractional scale factor; degenerate crops clamp to zero.

### 8.4 `apps/web/src/components/hub/CaptureControls.test.tsx` (new)

The shared icon-button row (§5.2). This is where the icon-only accessibility contract is pinned, because
it is the part a future refactor is most likely to quietly break:

- Every button is reachable by its accessible name (`getByRole("button", { name: ... })`) - the test would
  fail outright if an icon shipped without an `aria-label`.
- Each button carries the longer `title`, and it is a *different* string from the accessible name.
- With no capture area: both capture buttons are `aria-disabled`, are **not** `disabled` (so the tooltip
  can render), carry `screenshotCaptureNeedsArea` as `title`, and clicking them calls nothing.
- `aria-pressed` on the auto-capture button tracks the `autoCapture` prop, and its accessible name and
  title switch to the "on" strings when engaged.
- Clicking each button calls its handler exactly once.

### 8.5 `apps/web/src/components/hub/NotesPopover.test.tsx` and `apps/web/src/pages/NotesPopout.test.tsx` (extend)

Both render `CaptureControls` and both wire it up: the popover to the shell bridge, the pop-out to
`notesChannel`. Assert the wiring, not the rendering (8.4 owns that) - a click on the pop-out's
auto-capture button sends `{ type: "toggle-auto-capture" }`, and an inbound state message with
`autoCapture: true` shows the pressed state.

### 8.6 `apps/web/src/components/Recorder.test.tsx` (extend)

A capture carrying `ageMs` is stamped at `recordedNow - ageMs`; one without falls back to the receipt-time
stamp; a pathological `ageMs` larger than the elapsed recording clamps to 0 rather than going negative.

### 8.7 Manual calibration

The brief's §12 calibration harness is still worth having, in reduced form: a dev-only script that replays
a directory of PNG frames (captured at 1 Hz from a real presentation) through `slideDetector` and prints
commits per threshold pair. Frames, not video; no CLI polish; not shipped.

---

## 9. Non-goals

- OCR, slide titles, or any vision-model analysis of captures (the brief's §13).
- Aligning slides to diarized speaker turns as a data product. Screenshots already land at their capture
  offset in the transcript, which delivers the readable version of this.
- `occurrences` / repeat-visit tracking (§4.6).
- Masking regions (§2.2 - the capture-area picker covers it).
- Screen *recording* of any kind. Diariz records audio; this feature samples stills.
- Browser support. Auto-capture is desktop-shell only, like every other screenshot affordance.
- A configurable hotkey for the toggle (§5.5).
- Server-side changes. No new endpoint, entity, migration, or option.

---

## 10. Acceptance criteria

1. Cursor movement over a static slide for 60 s produces **exactly one** capture.
2. A 20-slide deck advanced at a normal pace produces one capture per slide, none mid-transition, verified
   by eye on a real recording.
3. A 60-minute recording with auto-capture engaged the whole time adds **under 5% CPU** on a typical
   4-core laptop, measured against the same recording without it. If a 1 Hz detection grab cannot meet
   this, `detectLongEdge` and `sampleIntervalMs` are the dials - do not skip the confirm step.
4. No capture is ever filed with an image that does not match the content at its recorded timestamp
   (the §4.4 confirm step, covered by 8.1).
5. Disengaging the toggle, ending the recording, or quitting the app leaves **no live interval timer**.
6. With auto-capture engaged, manual hotkey/tray/button captures still work and do not double-capture the
   current slide.
7. Every existing screenshot test passes unchanged - the IPC payload and everything downstream of it is
   untouched (except the optional `ageMs` field).
8. An auto-captured slide lands in the transcript at the moment it appeared, not `stableSamples` seconds
   later: with a 2 s stability window, a slide shown at 05:00 is filed at 05:00 plus or minus one sample
   interval, not at 05:02.
9. The button row fits on one line at the popover's 400 px width and in the pop-out notes window, with no
   wrap and no horizontal overflow, in both light and dark themes.
10. Every icon button announces a name to a screen reader and shows its longer description on hover -
    **including while disabled**, which is the state where the explanation matters most.

---

## 11. Release checklist impact

This is a **functional enhancement**: Minor +1, Build reset to 0 (e.g. `0.226.1` to `0.227.0`).

1. `version.json` plus all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`.
3. `CAPABILITIES` table in `releases.ts` - amend the existing **Meeting screenshots** row rather than
   adding one; this is a capture mode, not a separate feature. No new third-party library or model, so
   `AboutModal.tsx` disclaimers are unchanged.
4. README **Features** table - amend the **Meeting screenshots** row.
5. `docs/features.md` - amend the meeting-screenshots bullet, in lockstep with 4.
6. `docs/Overall_Synopsis_of_Platform.md` - the `RecorderState.paused` flag is a change to a documented
   cross-boundary contract (§7.2), so this needs an edit.
7. `docs/Data_Schema.md` - **no change** (no schema or storage change).
8. Help content - `apps/web/src/content/help/en/recording-hub.md` describes the screenshot workflow a user
   relies on, and both auto-capture and the icon buttons change it. Update that article (ASCII only,
   front-matter intact). **Check its images**: any help screenshot showing the three text buttons is now
   wrong. `apps/web/src/lib/help/images.ts` and its test gate what lives in `content/help/<locale>/images/`,
   so a stale or missing image fails the build rather than shipping quietly.

**Deployment surface: this needs a desktop release.** The detection logic, the ticker, and the tray item
all live in `apps/desktop/src/**`, so a new installer must be cut by pushing a `v*` tag (Windows NSIS via
CI; the macOS `.dmg` by hand). The web-side changes (the icon buttons, the shared `CaptureControls`
component, the `ageMs` handling) ship with a normal server redeploy and are picked up automatically. Note
the ordering consequence: **an installed older shell running the new web app gets the icon buttons but no
auto-capture** (the toggle is hidden without the shell prop, exactly as the whole section is hidden in a
browser) and keeps stamp-at-receipt timestamps. That degrades correctly and needs no version gate, but the
release notes should not claim auto-capture works until the installer is out.

---

## 12. Decisions taken

The four questions this spec originally left open, and their answers:

| Question | Decision |
|---|---|
| Timestamp accuracy | **Implement the hint** - as `ageMs` rather than an absolute timestamp, so no cross-process clock alignment is needed (§4.5). |
| Repeated slides | **Suppress duplicates** - a re-shown slide produces no second capture; `occurrences` tracking stays out of scope (§4.6). |
| Sample rate | **1 Hz with `stableSamples = 2`** - 2 s commit latency, and a slide must hold for 2 s to be captured (§6). |
| Label | **`Auto-capture`** (§3). |

One further change was folded in at the same time: **all three capture controls become icon buttons with
hover descriptions** (§5.1), because three text labels no longer fit the 400 px popover row. That carries
two consequences worth restating - the `disabled`-hides-`title` fix (without it an icon-only button with no
capture area set is a greyed glyph with no explanation), and extracting the popover's and pop-out window's
duplicated button rows into one shared component (§5.2).

---

## 13. Implementation plan

Inline execution, TDD throughout: each numbered step starts with a failing test, and the tree is green at
the end of every step. Steps within a phase are ordered by dependency, so they can be worked straight
through without backtracking.

### 13.0 Ship as two PRs

`main` is branch-protected and each PR ships exactly one release, so this lands as two:

| | Phase A - `feat/capture-controls-icons` | Phase B - `feat/auto-capture` |
|---|---|---|
| Scope | Icon buttons, shared `CaptureControls`, `aria-disabled` fix | Detector, ticker, tray toggle, `ageMs`, `paused` |
| Touches | `apps/web` only | `apps/desktop` + `apps/web` |
| Version bump | Build +1 (`0.226.1` to `0.226.2`) - a UI change, not a new capability | Minor +1 (`0.226.2` to `0.227.0`) - functional enhancement |
| Deployment | **Server redeploy only** | **Desktop release** (`v*` tag) plus redeploy |

The split is not bureaucratic: Phase A reaches every user the moment the server redeploys, while Phase B is
inert until an installer ships. Bundling them would hold a finished, independently-valuable fix (the
invisible "Set a capture area first" tooltip) behind an installer cut. Phase A is also reviewable as "no
behaviour change except the tooltip", which is a much easier review than the two mixed together.

If you would rather ship once, collapse them: do Phase A's steps first, skip its release checklist, and run
Phase B's checklist over the combined diff with a Minor bump.

---

### Phase A - icon capture controls (`apps/web` only)

**A1. i18n strings.** Add the new keys from §3 to **all four locales** (`en`, `de`, `es`, `fr` under
`apps/web/src/locales/*/workspace.json`). Not a test step, but it comes first so later steps can render
real strings rather than placeholders. Plain hyphens only, no em or en dashes.

**A2. `CaptureControls` (new component).**
- Write `apps/web/src/components/hub/CaptureControls.test.tsx` first, covering §8.4 in full. It fails
  because the module does not exist.
- Write `apps/web/src/components/hub/CaptureControls.tsx`. Props:
  `{ captureAreaSet: boolean; disabled?: boolean; onCapture: () => void; onChangeArea: () => void }`.
  Auto-capture's props are **not** added yet - Phase A has nothing to wire them to, and an unused prop is
  an invitation to render an inert button.
- Icons follow the pattern already established in [Recorder.tsx:117](apps/web/src/components/Recorder.tsx:117):
  a shared wrapper (`viewBox="0 0 24 24"`, `fill="none"`, `stroke="currentColor"`, `strokeWidth={2}`, round
  caps/joins, `aria-hidden`, `focusable="false"`) with each glyph as a small component. Size **16**, not
  Recorder's 18, to match `IconPopOut`/`IconClose` in the same popover.
- The `aria-disabled` treatment (§5.1) is the load-bearing detail: no `disabled` attribute anywhere, an
  early return in each handler, and `cursor: not-allowed` plus reduced opacity for the look.

**A3. Adopt it in the notes popover.** Replace the inline button row in
[NotesPopover.tsx](apps/web/src/components/hub/NotesPopover.tsx) with `CaptureControls`. Update
`NotesPopover.test.tsx` to assert the wiring only (the row's own behaviour is A2's). Existing assertions
that query by button text move to accessible name - expected churn, not a red flag.

**A4. Adopt it in the pop-out window.** Same swap in
[NotesPopout.tsx:107](apps/web/src/pages/NotesPopout.tsx:107). Note the pre-existing drift: the pop-out
also disables on `!live`, so pass `disabled={!live}` and keep `captureAreaSet` as the separate gate. Update
`NotesPopout.test.tsx`.

**A5. Release checklist.** `version.json` plus four mirrors, `RELEASES[0]`, and the help article's images
(§11 item 8) - any screenshot showing the old text buttons is now wrong. No `CAPABILITIES` / README /
`features.md` / architecture-doc change: nothing about what the app *does* changed. Say so in the PR.

**A6. Verify.** `npm test` and `npm run build` in `apps/web`. Then run the app and check the one thing no
test can: that the tooltip actually appears on hover over a greyed capture button. That is the whole point
of the `aria-disabled` change, and it is invisible to jsdom.

---

### Phase B - auto-capture (`apps/desktop` plus `apps/web`)

**B0. Spike the tick cost first.** Before building anything, measure `desktopCapturer.getSources()` with a
320 px `thumbnailSize` at 1 Hz, on a real multi-monitor machine, for ten minutes. Acceptance criterion 3
(under 5% CPU) is the one requirement in this spec that could invalidate the design rather than merely need
tuning - if a small-thumbnail grab costs the same as a full-resolution one because the OS composites the
whole screen either way, the answer is a lower sample rate or a different frame source, and that is much
cheaper to learn now than after the detector is written. Throwaway code, no tests (a spike is the explicit
TDD exception in CLAUDE.md) - delete it before the PR. **Stop and revisit §4 if it fails.**

**B1. `slideDetector.js` (new, pure).** The largest step and the one with the most test value.
- Write `apps/desktop/src/slideDetector.test.js` first: the full §8.1 table, plus `dhash` and `hamming`
  units. Build the synthetic bitmaps with a small in-test helper; commit no binary fixtures.
- Implement `dhash`, `hamming`, and `createDetector` (`observe` / `confirm` / `reject` / `isDuplicate`).
- Run `npm test` in `apps/desktop` (`node --test`). Everything here is pure, so this needs no Electron and
  no GPU and iterates fast.

**B2. `cropRectForSize` (`captureTarget.js`).** Test first per §8.3, then implement alongside the existing
`cropRectFor`. Small and self-contained; do it before touching `main.js` so the tick has its geometry ready.

**B3. Tray descriptors (`screenshotState.js`).** Test first per §8.2, then add the auto-capture checkbox to
`trayScreenshotItems`. Keep the existing `canCapture` as the single gate - do not introduce a second one.

**B4. The `paused` flag (cross-boundary).** Add `paused?: boolean` to `RecorderState` in
[trayRecorder.ts](apps/web/src/lib/trayRecorder.ts) and report it from `Recorder`, which already holds the
state ([Recorder.tsx:261](apps/web/src/components/Recorder.tsx:261)). Test on the web side that pausing and
resuming report it. On the shell side it is just a field on the recorder state object that the tick reads -
deliberately **not** a fifth `phase`, so `recorderState.js`'s existing switches need no change and no new
tests.

**B5. Wire `main.js`.** The only step with no direct unit coverage, which is exactly why B1-B3 came first:
by here the detector, the geometry, and the menu descriptors are all proven, and this is plumbing.
- The ticker (`setInterval` at `sampleIntervalMs`), created on engage and torn down on disengage, on
  recording end, and on quit. §10 criterion 5 is about this: **one owner, one teardown path.**
- The detection grab: small `thumbnailSize`, `cropRectForSize`, resize to 17x16, `getBitmap()`, `dhash`.
- The commit path per §4.4, including the confirm re-hash **through the same downsample chain** - extract
  that chain into one named helper used by both the tick and the confirm, or they will silently diverge and
  every commit will fail its own confirm.
- Dedupe, the `maxCaptures` self-disable (§7.3), and the failure handling in §7.4 (distinguish "the target
  is gone" from "this one grab threw"; three consecutive throws disengages).
- Tray item click handler, tooltip, and the engage notification.

**B6. Widen the IPC surface.** `preload.js` and [trayScreenshots.ts](apps/web/src/lib/trayScreenshots.ts):
`ageMs` on the captured payload, plus `toggleAutoCapture()` / `onAutoCaptureChanged()` mirroring the
existing `changeCaptureArea()` / `onCaptureAreaChanged()` pair. Every addition is optional-chained in
`trayScreenshots`, so a plain browser and an older shell both stay no-ops - that is the existing convention
in that file and it is what makes the mixed-version case in §11 degrade cleanly.

**B7. `Recorder.tsx`.** Test first per §8.6, then:
- The `ageMs` subtraction at the `capturedAtMs` stamp
  ([Recorder.tsx:702](apps/web/src/components/Recorder.tsx:702)) - the pause-aware clock is already
  `timing.elapsedMs(timingRef.current, Date.now())`, so this is `Math.max(0, elapsed - (shot.ageMs ?? 0))`.
- Auto-capture state mirrored from the shell, alongside the existing `captureAreaSet` mirror
  ([Recorder.tsx:784](apps/web/src/components/Recorder.tsx:784)), which is the pattern to copy.

**B8. `notesChannel`.** Test first: `autoCapture` on the state payload, `{ type: "toggle-auto-capture" }`
beside `{ type: "capture" }`, `onToggleAutoCapture` in the handlers, `toggleAutoCapture()` on the client.

**B9. The third button.** Extend `CaptureControls.test.tsx` for the auto-capture button (pressed state,
both label pairs, click), then add it to `CaptureControls.tsx` and pass the props through from both hosts
(§8.5). Doing this **last** means the button appears only once everything behind it works.

**B10. Release checklist.** All eight items in §11: version plus four mirrors, `RELEASES[0]`, the
`CAPABILITIES` **Meeting screenshots** row, the README Features row, the `docs/features.md` bullet in
lockstep, `Overall_Synopsis_of_Platform.md` for the `paused` contract, no `Data_Schema.md` change, and the
help article. State the desktop-release requirement explicitly in the PR body.

**B11. Verify.** Full suite - `dotnet test tests/Diariz.Api.Tests` (should be untouched, and confirming
that *is* §10 criterion 7), `npm test` in `apps/web` and `apps/desktop`. Then the parts only a human can
check:
- A real presentation, 20-ish slides: one capture per slide, none mid-transition (criterion 2).
- Cursor waggling over a static slide for a minute: exactly one capture (criterion 1).
- Slide timestamps land on the slide, not 2 s late (criterion 8).
- Back-navigation produces no duplicate.
- Pause and resume mid-deck.
- CPU measured against the same recording with auto-capture off (criterion 3).

**B12. Calibrate.** Save a 1 Hz PNG frame dump from the B11 presentation and run the §8.7 sweep over
`changeThreshold` x `stableSamples`. The §6 defaults are the brief's starting points and have never been
tested against a real deck; if the sweep moves them, update the table and say so in the PR.

---

### Risks, in the order they are likely to bite

1. **Tick cost** (B0). The one risk that can invalidate the design. Measured first, deliberately.
2. **Downsample-chain divergence** (B5). If the tick and the confirm hash through different paths, every
   commit fails its own confirm and the feature silently captures nothing. The symptom looks like a
   threshold problem and is not, which is what makes it expensive. One shared helper.
3. **Threshold defaults** (B12). Expected to move. Cheap to fix, and the §8.1 tests assert fixture
   behaviour rather than specific distances, so tuning does not invalidate them.
4. **`title` on an `aria-disabled` button** (A6). Verified by hand, because jsdom cannot see it.

---

## 14. B0 findings - the frame source has to change

**Measured on a three-display Windows machine (1920x1080 @1.25, 1920x1200 @1.25, 3840x2160 @2.5),
Electron 43.** Twenty interleaved samples, after a warm-up grab:

| Call | Mean | p50 | p90 |
|---|---|---|---|
| `getSources` @320px long edge (detection) | **428.4 ms** | 426.8 | 464.7 |
| `getSources` @2560px long edge (commit) | **442.5 ms** | 425.3 | 527.6 |
| `resize` to 17x16 + read bitmap | 0.2 ms | 0.2 | 0.3 |

A follow-up probe isolates the cause. Requesting a **1x1** thumbnail costs **445.7 ms** - the same as a
full-resolution one:

| Request | Sources returned | Mean |
|---|---|---|
| `types: ["screen"]` @1x1 | 3 | 445.7 ms |
| `types: ["screen"]` @320px | 3 | 485.7 ms |
| `types: ["screen"]` @full | 3 | 466.9 ms |
| `types: ["window"]` @320px | 24 | 2282.2 ms |

### What this invalidates

**§4.3's central optimisation does not exist.** `desktopCapturer.getSources()` composites and captures
*every* screen on each call; `thumbnailSize` only governs a final downscale, which is free. Sampling
small is therefore exactly as expensive as grabbing full resolution, and the sample/extract split saves
nothing.

At ~430 ms per call, a 1 Hz ticker costs **~43% of one core, continuously, for the length of the
meeting** - against acceptance criterion 3's budget of 5%. Slowing the tick does not rescue it either:
even one sample every 5 seconds is ~8.6% of a core, and a 5 s interval misses slides outright. **The
per-call cost has to go, not the call rate.**

### The direction that does work

Stop re-initiating a capture per sample and **hold one warm capture session**: a `getDisplayMedia` video
stream on the chosen display, opened when auto-capture engages and closed when it disengages, sampled
via `drawImage` onto a small canvas. The shell already auto-grants sources through
`setDisplayMediaRequestHandler` (that is how system-audio loopback works), so the existing capture-area
picker can still choose the display and hand the stream over without a second picker.

That relocates detection from the main process to the renderer, which **inverts §4.1** - but the reason
§4.1 gave for the main process (do not push a full-resolution PNG per second across IPC) is satisfied
even better there, because the frames never cross IPC at all. It also collapses three later steps:

- **§4.5's `ageMs` hint becomes unnecessary.** The renderer owns the pause-aware recorded clock, so it
  can stamp the candidate's first-seen moment directly. B6's IPC widening and B7's subtraction both go.
- **§7.2's `paused` cross-boundary flag becomes unnecessary.** The renderer already knows it is paused.
- **§4.4's commit-time confirm gets cheaper**, because the sample and the full-resolution frame come from
  the same video element rather than from two independent captures. The confirm is still wanted (the
  screen can change between frames) but the same-downsample-chain trap in B5 largely disappears.

**Not yet measured:** the baseline cost of holding a `getDisplayMedia` stream open for an hour, and
whether Windows shows a persistent screen-sharing indicator for it. Both need a second spike before this
is adopted - that is B0 round two, and it should run before any of B2/B5 is written.

### What survives unchanged

**§4.2, the detector, is independent of where frames come from** and is built (B1). Its tests assert
"how many slides came out of this footage", not distances or timings, so a change of frame source does
not touch them.
