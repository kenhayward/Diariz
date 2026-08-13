# Pop-out live-notes window (desktop shell)

Date: 2026-08-13
Status: design approved, not yet implemented

## Problem

A user on a single monitor cannot take notes during a call without keeping the whole Diariz window
visible. The call (Teams, Zoom, Meet) wants the screen; the notes panel is a popover anchored inside
the app's capture bar, so reaching it means giving the call's window up. The result is that live
notes - the feature most tied to a call actually being in progress - is the hardest one to use while
a call is in progress.

## Goal

Detach the live-notes panel into a small, always-on-top OS window that can float over a full-screen
call, without losing any of what the in-app panel can do.

## Scope

**In scope:** live notes taken *while recording* - the add/edit/delete list and the screenshot
capture + capture-area controls that already sit in that popover.

**Out of scope:** notes on a saved recording, folder notes, calendar-event prep notes, the transcript
and the summary. Those are read/write against the API from a normal page and have no single-monitor
problem; folding them in would turn a focused feature into a second application shell.

**Platform:** the Electron desktop shell only, on both Windows and macOS. Only the shell can pin a
window above another application, which is the entire point. A plain browser keeps today's inline
popover unchanged - a `window.open` popup would sync fine but could not float, so it would advertise
a fix for a problem it does not solve.

## Key constraint

Live notes are **not server state**. `Recorder.tsx` holds them in React state (`liveLines`), mirrors
them to IndexedDB (`lib/pendingNotes.ts`) so a crash cannot lose them, and POSTs them to the API only
after the audio uploads on stop. A second window therefore cannot be "a view that fetches notes" - it
has to reach the window that owns the recorder.

Reinforcing this: every shell-to-renderer event (`screenshot:captured`, `screenshot:area-changed`,
`tray:command`) is addressed to `mainWindow` specifically in `main.js`. The main window is already the
single renderer the shell talks to, and nothing here changes that.

## Architecture

The shell gains a fourth window kind (after main, setup, hotkey/picker): a small always-on-top
`BrowserWindow` loading **`{serverOrigin}/notes-popout`** - a new **top-level** React route outside
`WorkspaceLayout`, so it mounts no sidebar, no `Recorder`, no SignalR and no query prefetching.

Ownership is strictly one-way. The main window keeps everything it owns today: the `MediaRecorder`,
`liveLines` / `liveShots`, both IndexedDB stashes, the capture area, the recorded clock, and
attach-on-stop. **The pop-out is a remote control with no state of its own** beyond its draft input.

Two consequences make this cheap, and both are load-bearing:

- **The pop-out never stamps a timestamp.** `capturedAtMs` is produced by
  `timing.elapsedMs(timingRef.current, Date.now())` in the host and is pause-aware. The pop-out sends
  *text only*; the host stamps it. A second clock in a second window would drift silently the first
  time someone pauses.
- **The pop-out never talks to the API.** No auth plumbing, no `RequireAuth`, no login redirect
  inside a 380px window. It renders nothing until the host broadcasts a state snapshot, so "no host"
  degrades to an inert window rather than an information leak. The channel is same-origin by
  construction.

Transport is `BroadcastChannel("diariz.live-notes")` - same origin, same Electron session, so
`localStorage` and IndexedDB are shared too (not needed, but it is why auth is a non-problem).

### Rejected alternatives

**A native shell window (`notes.html`, like the existing `hotkey.html`) over IPC.** No web-app
changes and no origin dependency, but it means a second implementation of the note editor in plain
DOM - no i18n, no theming, no reuse of `NotesSection`. The two would drift the first time notes
change, and "full capabilities" is exactly what would be lost.

**Moving the recorder into the pop-out.** The `MediaRecorder`, both pending stashes and the
attach-on-stop path all live in the main window. Relocating them is a large, risky refactor for no
user-visible gain.

**A "compact mode" that shrinks and pins the main window.** Much the cheapest - same renderer, so no
sync problem at all. Rejected because it solves visibility without solving the workflow: you still
cannot glance at the recording list or a folder while the call runs, and toggling back mid-call is
disruptive. Retained as the fallback if the always-on-top spike (below) fails.

## Components

### New - web

| File | Purpose |
|---|---|
| `apps/web/src/lib/notesChannel.ts` | The protocol plus a thin channel wrapper. Exports `createNotesHost(handlers)` and `createNotesClient(handlers)`. Pure enough to unit-test against a fake channel. |
| `apps/web/src/pages/NotesPopout.tsx` | The routed page. Renders the existing `NotesSection` and the screenshot strip, nothing else. |

### New - desktop

| File | Purpose |
|---|---|
| `apps/desktop/src/notes-preload.js` | Narrow surface: `window.diarizNotes = { isPopout, setPinned, getPinned, close }`. Deliberately **not** `preload.js`: reusing that would register a second `onTrayCommand` listener, and a tray "stop" would then drive two recorders. |
| `apps/desktop/src/notesWindowState.js` | Pure model - default bounds, clamping remembered bounds to the currently attached displays, open-vs-focus decision. Follows the `recorderState.js` / `updateState.js` precedent so it is testable under `node --test`. |

### Changed

- `apps/desktop/src/main.js` - a `notesWindow` handle, `showNotesPopout()` / `closeNotesPopout()`,
  bounds persisted to `electron-store`, the same `setWindowOpenHandler` / `will-navigate` origin
  guards the main window uses, and teardown on quit and on main-window close.
- `apps/desktop/src/preload.js` - one addition, `openNotesPopout()`.
- `apps/web/src/components/hub/NotesPopover.tsx` - an optional `onPopOut` prop; the icon renders only
  when it is supplied, so a plain browser is untouched.
- `apps/web/src/components/Recorder.tsx` - wires the host side.
- `apps/web/src/App.tsx` - the `/notes-popout` route.

### Refactors carried by this work

**Extract `useLiveNotes()` from `Recorder.tsx`.** That file is already 1526 lines and this adds a
channel host to it. Moving the live-notes ownership (`liveLines`, `mirrorLines`, add/edit/delete, and
the new host) into a hook makes the new logic testable without mounting the whole recorder, and keeps
the file from growing past the point where it can be reasoned about. Scoped to notes; the audio path
is not touched.

**Make screenshot deletion id-based.** `deleteLiveShot(index)` is index-based. Across a window
boundary that is racy - a capture landing between the pop-out's render and the user's click shifts
the list, and the wrong capture is deleted. `PendingShot` already carries a stable `id`, so the
cross-window path uses it and the existing inline caller moves over with it rather than maintaining
two addressing schemes.

## Data flow

```
host (main window)                          client (pop-out)
  |                                            |
  |<---------------- hello ---------------------|  on mount, asks for a snapshot
  |----- state {lines, shots, flags, seq} ----->|  on every change, and in reply to a ping
  |<---------------- ping ----------------------|  every 2s, driven by the CLIENT (see below)
  |<------ add {text} / edit / delete ----------|  host stamps + mirrors to IndexedDB
  |<------ capture / changeArea ----------------|  host calls window.diariz.* exactly as today
  |<------ closing -----------------------------|  user closed the window -> restore inline
  |------- ended ------------------------------>|  recording stopped -> client closes itself
```

`state` carries `canCapture` and `captureAreaSet` as explicit flags rather than the client reading
`window.diariz` itself - the host is the authority, and the pop-out's preload deliberately does not
expose the capture bridge.

**The liveness poll is driven by the client, not the host.** This is counter-intuitive - the host owns
the data, so a host heartbeat is the obvious design - but it is wrong here, and measurably so (see the
spike results). Once the main window is hidden to the tray, Chromium throttles its timers; a host
heartbeat would slow down or stop while the host itself is perfectly healthy, and the pop-out would
declare a false disconnect in exactly the scenario the feature exists for. The pop-out is the visible,
unthrottled window, so it pings and treats a missing reply as the disconnect signal. Message *delivery*
to a hidden host is not throttled, so its reply is prompt.

Screenshot thumbnails cross the channel as `Blob`s, which are structured-cloneable; the
full-resolution PNG stays in the host's stash and is never sent.

### Lifecycle

- **Pop out** - the inline popover closes (notes live in exactly one place at a time) and the shell
  opens the window.
- **Stop recording** - the host sends `ended`, the window closes itself, and notes attach through the
  existing path untouched.
- **Next recording** - starts inline. The window *bounds* are remembered; the popped-out *state* is
  not, matching the explicit-button trigger rather than surprising the user with a window.
- **Main window closed to the tray while popped out** - the pop-out survives and keeps working. This
  is the feature's best case: the big app is gone, the small notes window remains. The hidden
  renderer keeps recording, and because `elapsedMs` is a `Date.now()` delta rather than a tick count,
  background throttling of the hidden window cannot skew the stamps.

## Error handling

| Case | Behaviour |
|---|---|
| Host reloads or crashes | Three client pings unanswered (~6s) - the pop-out shows a disconnected banner **and disables the input**. Notes already sent are safe (mirrored to IndexedDB before the crash); disabling matters because a note typed into a dead channel must not look accepted. |
| Pop-out closed by the user | `closing` - the host reopens the inline popover, nothing lost. |
| Pop-out killed without sending `closing` | The shell's `closed` handler notifies the host over IPC, giving the same restore. |
| Two pop-outs requested | `notesWindow` is a singleton handle; a second request focuses the existing window. |
| Remembered bounds on a monitor that is gone | `notesWindowState.js` clamps to an attached display before the window is created. |
| `BroadcastChannel` unavailable | Feature-detected: the host never offers the button, and the inline popover stays. |
| Plain browser | `window.diariz.openNotesPopout` is absent, so no button renders. No behaviour change. |

## Testing

TDD per CLAUDE.md - the failing test first, in every case.

| Test | Covers |
|---|---|
| `apps/web/src/lib/notesChannel.test.ts` | Host broadcasts on change; `hello` triggers a snapshot; each command dispatches to its handler; disconnect fires after the heartbeat window (fake timers). Fake channel, no jsdom needed. |
| `apps/web/src/pages/NotesPopout.test.tsx` | Renders nothing before the first `state`; Enter posts `add`; edit and delete post the right commands; the disconnected banner disables the input. |
| `apps/web/src/components/hub/NotesPopover.test.tsx` | Pop-out button absent without `onPopOut`, present and wired with it. |
| `apps/web/src/components/Recorder.test.tsx` (via `useLiveNotes`) | Popping out closes the inline popover; an inbound `add` is stamped from the recorded clock and mirrored to the stash. |
| `apps/desktop/src/notesWindowState.test.js` | Bounds clamping when a remembered monitor is gone; open-vs-focus. `node --test`, no Electron. |

### What the tests cannot prove

Two things had to be spiked **before** implementation, because the whole design rests on them. Both
are now resolved; the results are recorded below because one of them changed the design.

1. **`BroadcastChannel` crossing two Electron `BrowserWindow`s.** RESOLVED - see below.
2. **`alwaysOnTop` floating above a full-screen call on Windows.** RESOLVED - see below.

### Spike result: always-on-top over a full-screen window (2026-08-13, Windows 11, Electron 43)

**It holds, at Electron's default pin level.** `alwaysOnTop: true` is sufficient; the higher
`"screen-saver"` band is not needed, and should not be used - it also floats over the lock screen and
screensaver, which is more than this feature is entitled to.

Method: two pinned windows (default `"floating"` level and `"screen-saver"`) plus an **unpinned
control**, with a borderless full-screen window in a *separate process* raised over all three, then
the screen captured via `desktopCapturer`. The control is what makes the result meaningful: it was
buried, proving the full-screen window really did cover the screen, while both pinned windows stayed
readable. Without it the spike could not have failed - a full-screen window that never raised itself
would have left everything visible and looked identical to success.

Incidental finding worth carrying into implementation: `BrowserWindow.isVisible()` returned `true`
for the buried control. It reports "not hidden or minimised" and knows nothing about occlusion, so it
must not be used to reason about whether the pop-out is actually on screen.

**Residual gap.** The stand-in was a borderless full-screen window, which is the mode Teams, Zoom and
Meet use, so it is faithful for calls. Not covered: true exclusive-fullscreen DirectX (games, not
relevant here), and screen-share toolbars, which some conferencing apps pin topmost themselves - two
topmost windows are ordered by which was raised last, so such a toolbar could overlap the pop-out,
but could not bury it. Worth one confirmation against a real call before release.

### Spike result: BroadcastChannel across windows (2026-08-13, Windows 11, Electron 43)

**The protocol carries.** Two `BrowserWindow`s served from one origin over HTTP completed the full
round trip: `hello` produced a snapshot, `add` reached the host, the host stamped the line, and the
stamped line came back to the client. A `Blob` standing in for a screenshot thumbnail survived the
structured clone intact, and a window did **not** receive its own broadcasts - so a single channel
object per window is enough to stop the host reprocessing the state it just sent.

The control was a third window loading the byte-identical client page from `127.0.0.1` instead of
`localhost` - a different origin to Chromium. It heard nothing, which is what shows the messages
travelled the origin-scoped channel rather than arriving by some other route. Serving over real HTTP
mattered for the same reason: `data:` and `file:` documents get opaque origins and would have failed
for a reason unrelated to Electron.

### Spike result: hidden host, and why the heartbeat inverted

Re-run with the shell's real window settings (no `backgroundThrottling` override) and the host
**hidden**, as close-to-tray leaves it:

- **Messaging to a hidden host is not throttled.** The command reached it, the state came back, and
  the line was stamped correctly. The tray scenario works.
- **Host timers ARE throttled.** A 250 ms `setInterval` in the hidden host fired **4 times in 4
  seconds instead of 16** - clamped to roughly 1 Hz.

The second point is why the liveness poll moved to the client. A host-side heartbeat would degrade
precisely when the main window is in the tray, and the pop-out would report a disconnect for a host
that is working fine.

**Limitation of this spike, stated plainly:** Chromium's *intensive* throttling (timers clamped to
about once per minute) only engages after roughly five minutes hidden, and a four-second spike cannot
reproduce it. A call lasts far longer than five minutes, so a host-driven heartbeat would have failed
worse in production than anything measured here. The client-driven poll sidesteps the whole regime
rather than relying on a threshold nobody has measured under real conditions.

**Pre-existing issue noticed, not introduced by this work:** `Recorder.tsx` runs the display ticker
and the auto-stop watcher on `setInterval`, and both are subject to the same throttling while the
window sits in the tray. The display recovers on its own (`elapsedMs` is a `Date.now()` delta), and
the auto-stop watcher compares against wall-clock time so it stops at the right moment rather than
drifting - but under intensive throttling it could *notice* up to a minute late. Worth its own look;
deliberately out of scope here.

## Release surface

Functional enhancement, so **Minor +1 and Build reset to 0**, with `version.json` and its four
mirrors in lockstep, plus the `RELEASES[0]` entry.

Also required in the same PR:

- a `CAPABILITIES` row (new user-facing capability),
- a README Features table row and the matching `docs/features.md` bullet,
- a section in `docs/Overall_Synopsis_of_Platform.md` - this adds both a new shell window and a new
  cross-window contract,
- an edit to the "notes during a call" help article, since behaviour a user relies on changes.

`docs/Data_Schema.md` is untouched: no schema, storage or migration change.

**Deployment: this needs both a desktop release and a server redeploy.** It touches
`apps/desktop/src/**` (new window, new preload) *and* `apps/web` (new route and components). An older
installed desktop app picks up the web half and simply never shows the pop-out button, which degrades
correctly.
