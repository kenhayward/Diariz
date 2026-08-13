# Pop-out Live-Notes Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user on one monitor detach the live-notes panel into a small always-on-top desktop
window that floats over a full-screen call.

**Architecture:** The Electron shell opens a second `BrowserWindow` at `{serverOrigin}/notes-popout`,
a top-level React route outside the workspace layout. The main window stays the sole owner of the
recorder, the note lines, the screenshot stash and the recorded clock; the pop-out is a remote control
that talks to it over a same-origin `BroadcastChannel`. The pop-out never calls the API and never
stamps a timestamp.

**Tech Stack:** React 19 + TypeScript + Vite (`apps/web`), Vitest + @testing-library/react, Electron 43
(`apps/desktop`), `node --test` for the shell's pure models.

**Spec:** `docs/superpowers/specs/2026-08-13-notes-popout-window-design.md`. Read it first - in
particular the two spike results, which are why two things below look backwards.

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail for the right reason, then write
  the minimal code. No production code without a preceding failing test.
- **No em or en dashes in user-facing text.** Use a plain hyphen `-` in UI strings, i18n catalogues,
  release notes and help articles. Code and internal docs are unaffected.
- **i18n catalogues come in four locales:** `apps/web/src/locales/{en,de,es,fr}/workspace.json`. A key
  added to one must be added to all four.
- **Never run `git add -A` in this repository.** It sweeps agent scratch files into the commit. Stage
  explicit paths, always.
- **Pin level is the Electron default.** Use `alwaysOnTop: true`. Do NOT call
  `setAlwaysOnTop(win, "screen-saver")` - the spike showed the default suffices, and the higher band
  also floats over the lock screen.
- **The pop-out never computes `capturedAtMs`.** It sends text; the host stamps. The recorded clock is
  pause-aware and exists only in the host.
- **`BrowserWindow.isVisible()` must not be used to reason about the pop-out being on screen.** It
  reports "not hidden or minimised" and returned `true` for a fully buried window during the spike.
- **Version bump:** this is a functional enhancement, so Minor +1 and Build reset to 0.
  `0.210.1` -> **`0.211.0`**, in `version.json` plus its four mirrors (Task 8).
- **Test output must stay pristine** - a passing run has no errors or warnings.

## File Structure

**Created - web**

| File | Responsibility |
|---|---|
| `apps/web/src/components/hub/ShotStrip.tsx` | The capture thumbnail strip, extracted so the popover and the pop-out share one copy. |
| `apps/web/src/lib/notesChannel.ts` | The cross-window protocol: message types, `createNotesHost`, `createNotesClient`. No React, no DOM beyond the channel. |
| `apps/web/src/lib/useNotesPopout.ts` | Host-side lifecycle: `poppedOut` state, host creation, publish-on-change. |
| `apps/web/src/pages/NotesPopout.tsx` | The routed pop-out page. |

**Created - desktop**

| File | Responsibility |
|---|---|
| `apps/desktop/src/notesWindowState.js` | Pure model: bounds defaults and clamping, open-vs-focus. |
| `apps/desktop/src/notes-preload.js` | Narrow bridge for the pop-out window. Deliberately not `preload.js`. |

**Modified**

| File | Change |
|---|---|
| `apps/web/src/lib/types.ts` | Add the `ShotView` interface. |
| `apps/web/src/components/hub/NotesPopover.tsx` | Use `ShotStrip`; add the optional `onPopOut` prop; delete shots by id. |
| `apps/web/src/components/Recorder.tsx` | Delete shots by id; call `useNotesPopout`; pass `onPopOut`. |
| `apps/web/src/App.tsx` | Add the `/notes-popout` route. |
| `apps/web/src/locales/{en,de,es,fr}/workspace.json` | New strings. |
| `apps/desktop/src/main.js` | Notes window lifecycle + IPC. |
| `apps/desktop/src/preload.js` | `openNotesPopout` + `onNotesPopoutClosed`. |

---

### Task 1: Address captures by id, and extract the thumbnail strip

Today `NotesPopover` renders the capture thumbnails inline and deletes them by **array index**
(`onDeleteShot(index)`). Across a window boundary an index is racy: a capture landing between the
pop-out's render and the user's click shifts the list and the wrong capture is deleted. `PendingShot`
already carries a stable `id`, so this switches to it and lifts the strip into a component both
surfaces can use.

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Create: `apps/web/src/components/hub/ShotStrip.tsx`
- Create: `apps/web/src/components/hub/ShotStrip.test.tsx`
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`
- Modify: `apps/web/src/components/Recorder.tsx` (the `deleteLiveShot` function, around line 745)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ShotView` (`{ id: string; capturedAtMs: number; thumb: Blob }`) exported from
  `lib/types.ts`; `ShotStrip` with props `{ shots: ShotView[]; onDelete: (id: string) => void }`;
  `NotesPopover`'s `onDeleteShot` changes to `(id: string) => void`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/hub/ShotStrip.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ShotStrip from "./ShotStrip";
import type { ShotView } from "../../lib/types";

const shot = (over: Partial<ShotView> = {}): ShotView => ({
  id: "s1",
  capturedAtMs: 61_000,
  thumb: new Blob(["x"], { type: "image/jpeg" }),
  ...over,
});

describe("ShotStrip", () => {
  it("renders one thumbnail per capture, labelled with its stamp", () => {
    render(<ShotStrip shots={[shot(), shot({ id: "s2", capturedAtMs: 3_904_000 })]} onDelete={vi.fn()} />);
    expect(screen.getByAltText(/1:01/)).toBeTruthy();
    expect(screen.getByAltText(/1:05:04/)).toBeTruthy();
  });

  // The reason this component exists: deleting must name the capture, not its position.
  it("deletes by id, not by index", () => {
    const onDelete = vi.fn();
    render(<ShotStrip shots={[shot(), shot({ id: "s2" })]} onDelete={onDelete} />);
    fireEvent.click(screen.getAllByRole("button", { name: /delete/i })[1]);
    expect(onDelete).toHaveBeenCalledWith("s2");
  });
});
```

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/components/hub/ShotStrip.test.tsx
```

Expected: FAIL - cannot resolve `./ShotStrip`.

- [ ] **Step 3: Add the `ShotView` type**

In `apps/web/src/lib/types.ts`, next to `MeetingNote` (around line 319), add:

```ts
/// The part of a captured screenshot the notes UI needs: enough to show a thumbnail and delete it by
/// name. `PendingShot` (lib/pendingScreenshots.ts) satisfies this structurally, and it is also what
/// crosses the pop-out channel - the full-resolution PNG never leaves the host.
export interface ShotView {
  id: string;
  capturedAtMs: number;
  thumb: Blob;
}
```

- [ ] **Step 4: Write `ShotStrip`**

Create `apps/web/src/components/hub/ShotStrip.tsx`. The object-URL handling is lifted verbatim from
`NotesPopover` - including the revoke on cleanup, which stops a long meeting leaking one URL per
capture.

```tsx
import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { formatDuration } from "../../lib/format";
import type { ShotView } from "../../lib/types";

/**
 * The live thumbnail strip for captures taken during a recording. Shared by the in-app notes popover
 * and the pop-out window, which is why it takes `ShotView` (id + stamp + thumbnail) rather than the
 * full `PendingShot`: the pop-out never receives the full-resolution image.
 *
 * Deletion is by id. An index would be wrong here - a capture arriving between render and click
 * shifts the list, and in the pop-out that gap is a whole window boundary wide.
 */
export default function ShotStrip({
  shots,
  onDelete,
}: {
  shots: ShotView[];
  onDelete: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");

  // Recomputed whenever the capture set changes; the previous batch is revoked on cleanup.
  const previews = useMemo(() => shots.map((s) => URL.createObjectURL(s.thumb)), [shots]);
  useEffect(() => () => previews.forEach((url) => URL.revokeObjectURL(url)), [previews]);

  return (
    <ul style={{ display: "flex", flexWrap: "wrap", gap: 4, margin: 0, padding: 0, listStyle: "none" }}>
      {previews.map((url, i) => (
        <li key={shots[i].id} style={{ position: "relative" }}>
          <img
            src={url}
            alt={t("screenshotAlt", { time: formatDuration(shots[i].capturedAtMs) })}
            style={{ display: "block", height: 56, width: "auto", borderRadius: 6, border: "1px solid var(--hub-border)" }}
          />
          <button
            type="button"
            aria-label={t("screenshotDelete")}
            onClick={() => onDelete(shots[i].id)}
            style={{
              position: "absolute", top: -4, right: -4,
              display: "flex", alignItems: "center", justifyContent: "center",
              width: 16, height: 16, borderRadius: "50%", border: "none",
              background: "var(--hub-popover-bg)", color: "var(--hub-red-text)",
              fontSize: 11, lineHeight: 1, boxShadow: "0 1px 3px rgba(0, 0, 0, 0.3)", cursor: "pointer",
            }}
          >
            ✕
          </button>
        </li>
      ))}
    </ul>
  );
}
```

- [ ] **Step 5: Run the test and verify it passes**

```bash
cd apps/web && npx vitest run src/components/hub/ShotStrip.test.tsx
```

Expected: PASS (2 tests).

- [ ] **Step 6: Use `ShotStrip` in `NotesPopover` and switch its prop to an id**

In `apps/web/src/components/hub/NotesPopover.tsx`:

1. Change the prop type: `onDeleteShot: (index: number) => void;` becomes
   `onDeleteShot: (id: string) => void;`
2. Delete the `previews` `useMemo` and its `useEffect` cleanup (lines 60-61) - they moved to `ShotStrip`.
3. Delete the `formatDuration` and `useMemo`/`useEffect` imports if nothing else uses them; keep
   `useTranslation`.
4. Replace the entire `<ul>...</ul>` thumbnail block with:

```tsx
<ShotStrip shots={shots} onDelete={onDeleteShot} />
```

5. Add the import: `import ShotStrip from "./ShotStrip";`

- [ ] **Step 7: Switch `deleteLiveShot` to an id in `Recorder.tsx`**

Replace the `deleteLiveShot` function (around line 745) with:

The current body (line 746) is:

```tsx
  function deleteLiveShot(index: number) {
    const shot = liveShotsRef.current[index];
    if (!shot) return;
    const next = liveShotsRef.current.filter((_, i) => i !== index);
    liveShotsRef.current = next;
    setLiveShots(next);
    if (userId) void removePendingScreenshot(userId, shot.id);
  }
```

Replace it with, keeping the existing doc comment above it and extending it as shown:

```tsx
  /// The per-capture delete button. Filters the *current* ref, not a value captured at render time, so
  /// a rapid string of deletes (or a delete racing an incoming capture) always removes the right item
  /// rather than one computed against a stale array. Removes just that one record from IndexedDB, not
  /// a rewrite of the remaining set.
  ///
  /// Addressed by id rather than position: the pop-out window renders its own copy of this strip, so
  /// the gap between render and click can be a whole window boundary wide.
  function deleteLiveShot(id: string) {
    const next = liveShotsRef.current.filter((s) => s.id !== id);
    if (next.length === liveShotsRef.current.length) return;
    liveShotsRef.current = next;
    setLiveShots(next);
    if (userId) void removePendingScreenshot(userId, id);
  }
```

- [ ] **Step 8: Run the full web suite**

```bash
cd apps/web && npm test
```

Expected: PASS. If `NotesPopover.test.tsx` asserted an index argument, update that assertion to the id -
the behaviour change is deliberate.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/hub/ShotStrip.tsx apps/web/src/components/hub/ShotStrip.test.tsx apps/web/src/components/hub/NotesPopover.tsx apps/web/src/components/hub/NotesPopover.test.tsx apps/web/src/components/Recorder.tsx
git commit -m "refactor(notes): address captures by id and extract the thumbnail strip"
```

---

### Task 2: The cross-window protocol

The whole sync design in one module, with no React and no window plumbing, so it can be tested against
a fake channel that mirrors real `BroadcastChannel` semantics.

**The liveness poll runs from the client, and that is not a slip.** The spike measured a hidden host's
timers being clamped to about 1 Hz, so a host-driven heartbeat would slow down exactly when the main
window is in the tray and the pop-out would declare a false disconnect. The pop-out is the visible,
unthrottled window, so it pings and the host answers.

**Files:**
- Create: `apps/web/src/lib/notesChannel.ts`
- Create: `apps/web/src/lib/notesChannel.test.ts`

**Interfaces:**
- Consumes: `MeetingNote` and `ShotView` from `lib/types.ts` (Task 1).
- Produces: `NOTES_CHANNEL`, `NotesState`, `ChannelLike`, `NotesHostHandlers`, `NotesHost`,
  `createNotesHost`, `NotesClientHandlers`, `NotesClient`, `createNotesClient`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/notesChannel.test.ts`:

```ts
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createNotesHost, createNotesClient, type ChannelLike, type NotesState } from "./notesChannel";
import type { MeetingNote } from "./types";

/// A fake BroadcastChannel bus. Delivers to every OTHER channel on the bus and never to the sender -
/// the semantics the Electron spike confirmed, and which the host relies on so it does not reprocess
/// the state it just published.
function makeBus() {
  const channels: FakeChannel[] = [];
  class FakeChannel implements ChannelLike {
    onmessage: ((e: { data: unknown }) => void) | null = null;
    closed = false;
    postMessage(data: unknown) {
      for (const c of channels) if (c !== this && !c.closed) c.onmessage?.({ data });
    }
    close() { this.closed = true; }
  }
  return () => { const c = new FakeChannel(); channels.push(c); return c; };
}

const line = (over: Partial<MeetingNote> = {}): MeetingNote => ({
  id: "n1", text: "hello", capturedAtMs: 1_000, ordinal: 0,
  createdAt: "2026-08-13T10:00:00.000Z", ...over,
});

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [line()], shots: [], canCapture: false, captureAreaSet: false, recording: true, ...over,
});

const noopHandlers = {
  onAdd: vi.fn(), onEdit: vi.fn(), onDelete: vi.fn(), onDeleteShot: vi.fn(),
  onCapture: vi.fn(), onChangeArea: vi.fn(), onClientClosed: vi.fn(),
};

beforeEach(() => { vi.useFakeTimers(); vi.clearAllMocks(); });
afterEach(() => { vi.useRealTimers(); });

describe("notesChannel", () => {
  it("answers a client's hello with the current state", () => {
    const channel = makeBus();
    const current = state();
    createNotesHost({ ...noopHandlers, getState: () => current }, { channel: channel() });
    const onState = vi.fn();
    createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });
    expect(onState).toHaveBeenCalledWith(current);
  });

  it("routes a client's add to the host, and republishes", () => {
    const channel = makeBus();
    let lines = [line()];
    const onAdd = vi.fn((text: string) => { lines = [...lines, line({ id: "n2", text })]; });
    const host = createNotesHost(
      { ...noopHandlers, onAdd, getState: () => state({ lines }) },
      { channel: channel() },
    );
    const onState = vi.fn();
    const client = createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    client.add("typed in the pop-out");

    expect(onAdd).toHaveBeenCalledWith("typed in the pop-out");
    // The host republishes so the pop-out sees the line it just wrote, stamped by the host.
    host.publish();
    expect(onState).toHaveBeenLastCalledWith(state({ lines }));
  });

  it("reports a disconnect after three unanswered pings", () => {
    const channel = makeBus();
    const onDisconnected = vi.fn();
    // No host on the bus at all.
    createNotesClient({ onState: vi.fn(), onEnded: vi.fn(), onDisconnected }, { channel: channel(), pingMs: 2_000 });

    vi.advanceTimersByTime(2_000);
    expect(onDisconnected).not.toHaveBeenCalled();
    vi.advanceTimersByTime(4_000);
    expect(onDisconnected).toHaveBeenCalledTimes(1);
  });

  it("stays connected while a host answers the pings", () => {
    const channel = makeBus();
    createNotesHost({ ...noopHandlers, getState: () => state() }, { channel: channel() });
    const onDisconnected = vi.fn();
    createNotesClient({ onState: vi.fn(), onEnded: vi.fn(), onDisconnected }, { channel: channel(), pingMs: 2_000 });

    vi.advanceTimersByTime(20_000);
    expect(onDisconnected).not.toHaveBeenCalled();
  });

  it("tells the client when the recording ended", () => {
    const channel = makeBus();
    const host = createNotesHost({ ...noopHandlers, getState: () => state() }, { channel: channel() });
    const onEnded = vi.fn();
    createNotesClient({ onState: vi.fn(), onEnded, onDisconnected: vi.fn() }, { channel: channel() });

    host.end();

    expect(onEnded).toHaveBeenCalledTimes(1);
  });

  it("tells the host when the client window is closing", () => {
    const channel = makeBus();
    const onClientClosed = vi.fn();
    createNotesHost({ ...noopHandlers, onClientClosed, getState: () => state() }, { channel: channel() });
    const client = createNotesClient({ onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    client.close();

    expect(onClientClosed).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/lib/notesChannel.test.ts
```

Expected: FAIL - cannot resolve `./notesChannel`.

- [ ] **Step 3: Write the module**

Create `apps/web/src/lib/notesChannel.ts`:

```ts
/// The pop-out notes protocol. The main window (the HOST) owns the recorder, the note lines, the
/// screenshot stash and the recorded clock; the pop-out window (the CLIENT) owns nothing but its
/// draft input. Everything the client wants done, it asks the host to do.
///
/// Transport is BroadcastChannel, which is scoped to the origin - and both windows load the web app
/// from the same server origin, so no extra auth or wiring is involved. A window does not receive its
/// own broadcasts, which is what lets the host publish and listen on one channel object.
///
/// The liveness poll is driven by the CLIENT, which looks backwards until you know why: once the main
/// window is hidden to the tray, Chromium throttles its timers (measured at roughly 1 Hz, and far
/// worse after five minutes hidden), so a host heartbeat would stall exactly when the feature is being
/// used and the pop-out would report a disconnect for a healthy host. Message *delivery* to a hidden
/// host is not throttled, so the host's replies are prompt. The visible window therefore does the
/// polling.

import type { MeetingNote, ShotView } from "./types";

export const NOTES_CHANNEL = "diariz.live-notes";

/// Everything the pop-out renders. Rebuilt by the host on every publish.
export interface NotesState {
  lines: MeetingNote[];
  shots: ShotView[];
  /// Whether this shell can capture screenshots at all. Sent as a flag rather than read from
  /// `window.diariz` in the client: the pop-out's preload deliberately does not expose the capture
  /// bridge, and the host is the authority either way.
  canCapture: boolean;
  captureAreaSet: boolean;
  recording: boolean;
}

type HostMessage = { type: "state"; state: NotesState } | { type: "ended" };

type ClientMessage =
  | { type: "hello" }
  | { type: "ping" }
  | { type: "add"; text: string }
  | { type: "edit"; id: string; text: string }
  | { type: "delete"; id: string }
  | { type: "deleteShot"; id: string }
  | { type: "capture" }
  | { type: "changeArea" }
  | { type: "closing" };

/// The slice of BroadcastChannel used here, so tests can supply a fake.
export interface ChannelLike {
  postMessage(data: unknown): void;
  close(): void;
  onmessage: ((e: { data: unknown }) => void) | null;
}

function open(channel?: ChannelLike): ChannelLike {
  return channel ?? new BroadcastChannel(NOTES_CHANNEL);
}

// ---- Host (the main window) ----

export interface NotesHostHandlers {
  onAdd(text: string): void;
  onEdit(id: string, text: string): void;
  onDelete(id: string): void;
  onDeleteShot(id: string): void;
  onCapture(): void;
  onChangeArea(): void;
  /// The pop-out window is going away; restore the inline popover. Must be idempotent - it can arrive
  /// both from the client's own `closing` message and from the shell noticing the window closed.
  onClientClosed(): void;
  getState(): NotesState;
}

export interface NotesHost {
  /// Broadcast the current state. Call whenever anything in it changes.
  publish(): void;
  /// Tell the pop-out the recording is over, so it can close itself.
  end(): void;
  dispose(): void;
}

export function createNotesHost(
  handlers: NotesHostHandlers,
  opts: { channel?: ChannelLike } = {},
): NotesHost {
  const ch = open(opts.channel);

  const publish = () => {
    const message: HostMessage = { type: "state", state: handlers.getState() };
    ch.postMessage(message);
  };

  ch.onmessage = (e) => {
    const m = e.data as ClientMessage;
    switch (m.type) {
      // A ping is answered with the full state rather than a bare pong: it costs the same round trip
      // and it self-heals a client that missed an update while the host was busy.
      case "hello":
      case "ping":
        publish();
        break;
      case "add": handlers.onAdd(m.text); break;
      case "edit": handlers.onEdit(m.id, m.text); break;
      case "delete": handlers.onDelete(m.id); break;
      case "deleteShot": handlers.onDeleteShot(m.id); break;
      case "capture": handlers.onCapture(); break;
      case "changeArea": handlers.onChangeArea(); break;
      case "closing": handlers.onClientClosed(); break;
    }
  };

  return {
    publish,
    end: () => ch.postMessage({ type: "ended" } satisfies HostMessage),
    dispose: () => { ch.onmessage = null; ch.close(); },
  };
}

// ---- Client (the pop-out window) ----

export interface NotesClientHandlers {
  onState(state: NotesState): void;
  onEnded(): void;
  /// Fired once when the host stops answering. A later `state` silently reconnects.
  onDisconnected(): void;
}

export interface NotesClient {
  add(text: string): void;
  edit(id: string, text: string): void;
  remove(id: string): void;
  removeShot(id: string): void;
  capture(): void;
  changeArea(): void;
  /// Tell the host this window is going away.
  close(): void;
  dispose(): void;
}

export function createNotesClient(
  handlers: NotesClientHandlers,
  opts: { channel?: ChannelLike; pingMs?: number } = {},
): NotesClient {
  const ch = open(opts.channel);
  const pingMs = opts.pingMs ?? 2_000;
  let missed = 0;
  let reported = false;

  ch.onmessage = (e) => {
    const m = e.data as HostMessage;
    if (m.type === "ended") { handlers.onEnded(); return; }
    if (m.type !== "state") return;
    missed = 0;
    reported = false;
    handlers.onState(m.state);
  };

  const send = (m: ClientMessage) => ch.postMessage(m);

  // Three unanswered pings, not one: a single missed round trip during a busy render is not a dead
  // host, and a banner that flickers on every hiccup trains the user to ignore it.
  const timer = setInterval(() => {
    missed += 1;
    if (missed >= 3 && !reported) { reported = true; handlers.onDisconnected(); }
    send({ type: "ping" });
  }, pingMs);

  send({ type: "hello" });

  return {
    add: (text) => send({ type: "add", text }),
    edit: (id, text) => send({ type: "edit", id, text }),
    remove: (id) => send({ type: "delete", id }),
    removeShot: (id) => send({ type: "deleteShot", id }),
    capture: () => send({ type: "capture" }),
    changeArea: () => send({ type: "changeArea" }),
    close: () => send({ type: "closing" }),
    dispose: () => { clearInterval(timer); ch.onmessage = null; ch.close(); },
  };
}
```

- [ ] **Step 4: Run the test and verify it passes**

```bash
cd apps/web && npx vitest run src/lib/notesChannel.test.ts
```

Expected: PASS (6 tests).

- [ ] **Step 5: Mutation-check the disconnect test**

A liveness test that cannot fail is worse than none. Temporarily change `missed >= 3` to
`missed >= 999`, re-run, and confirm **"reports a disconnect after three unanswered pings"** FAILS.
Then change it back and confirm the suite is green again.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/lib/notesChannel.ts apps/web/src/lib/notesChannel.test.ts
git commit -m "feat(notes): add the pop-out cross-window protocol"
```

---

### Task 3: The pop-out page

**Files:**
- Create: `apps/web/src/pages/NotesPopout.tsx`
- Create: `apps/web/src/pages/NotesPopout.test.tsx`
- Modify: `apps/web/src/App.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: `createNotesClient`, `NotesState` (Task 2); `ShotStrip`, `ShotView` (Task 1);
  `NotesSection` (existing, props `{ notes, onAdd?, onEdit?, onDelete?, onJump? }`).
- Produces: the route `/notes-popout`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/pages/NotesPopout.test.tsx`:

```tsx
import { render, screen, fireEvent, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import NotesPopout from "./NotesPopout";
import type { NotesState } from "../lib/notesChannel";

// One client instance shared with the test, so the test can drive the page as the host would.
const client = {
  add: vi.fn(), edit: vi.fn(), remove: vi.fn(), removeShot: vi.fn(),
  capture: vi.fn(), changeArea: vi.fn(), close: vi.fn(), dispose: vi.fn(),
};
let handlers: { onState: (s: NotesState) => void; onEnded: () => void; onDisconnected: () => void };

vi.mock("../lib/notesChannel", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/notesChannel")>()),
  createNotesClient: (h: typeof handlers) => { handlers = h; return client; },
}));

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [{ id: "n1", text: "First point", capturedAtMs: 61_000, ordinal: 0, createdAt: "2026-08-13T10:00:00.000Z" }],
  shots: [], canCapture: false, captureAreaSet: false, recording: true, ...over,
});

beforeEach(() => vi.clearAllMocks());

describe("NotesPopout", () => {
  it("shows a waiting message until the host answers", () => {
    render(<NotesPopout />);
    expect(screen.getByText(/waiting for the main/i)).toBeTruthy();
    expect(screen.queryByText("First point")).toBeNull();
  });

  it("renders the host's lines once state arrives", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));
    expect(screen.getByText("First point")).toBeTruthy();
  });

  it("sends typed text to the host rather than adding it locally", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "Second point" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(client.add).toHaveBeenCalledWith("Second point");
    // The host owns the list; nothing appears until it publishes.
    expect(screen.queryByText("Second point")).toBeNull();
  });

  it("disables the input when the host stops answering", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));
    act(() => handlers.onDisconnected());
    expect(screen.getByText(/lost contact/i)).toBeTruthy();
    expect(screen.getByPlaceholderText(/add a note/i).hasAttribute("disabled")).toBe(true);
  });

  it("hides the capture controls when the shell cannot capture", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state({ canCapture: false })));
    expect(screen.queryByRole("button", { name: /capture/i })).toBeNull();
  });
});
```

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/pages/NotesPopout.test.tsx
```

Expected: FAIL - cannot resolve `./NotesPopout`.

- [ ] **Step 3: Add the i18n strings**

Add these keys to `apps/web/src/locales/en/workspace.json` (alphabetical placement is not enforced;
put them beside the existing `liveNotes*` keys around line 234):

```json
  "notesPopOut": "Open in a separate window",
  "notesPopoutWaiting": "Waiting for the main Diariz window...",
  "notesPopoutDisconnected": "Lost contact with the main Diariz window. Bring it back to keep taking notes.",
```

Add the same three keys to `de`, `es` and `fr`. Translations:

- de: `"In einem separaten Fenster oeffnen"` / `"Warten auf das Diariz-Hauptfenster..."` /
  `"Kein Kontakt zum Diariz-Hauptfenster. Hole es zurueck, um weiter Notizen zu machen."`
- es: `"Abrir en una ventana aparte"` / `"Esperando a la ventana principal de Diariz..."` /
  `"Se perdio el contacto con la ventana principal de Diariz. Vuelve a abrirla para seguir tomando notas."`
- fr: `"Ouvrir dans une fenetre separee"` / `"En attente de la fenetre principale de Diariz..."` /
  `"Contact perdu avec la fenetre principale de Diariz. Rouvrez-la pour continuer a prendre des notes."`

Use plain hyphens only. If the surrounding catalogue uses accented characters, match it - the
ASCII-only rule applies to help articles, not to these catalogues.

- [ ] **Step 4: Write the page**

Create `apps/web/src/pages/NotesPopout.tsx`:

```tsx
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import NotesSection from "../components/NotesSection";
import ShotStrip from "../components/hub/ShotStrip";
import { createNotesClient, type NotesClient, type NotesState } from "../lib/notesChannel";

/**
 * The detached live-notes window, loaded by the desktop shell at /notes-popout.
 *
 * It owns nothing. Every line it shows came from the main window, and every edit goes back there to be
 * applied - including the timestamp, which only the host can produce because only the host knows the
 * recorded (pause-aware) clock. It never calls the API, which is why it needs no auth: with no host on
 * the channel it simply renders the waiting state.
 */
export default function NotesPopout() {
  const { t } = useTranslation("workspace");
  const [state, setState] = useState<NotesState | null>(null);
  const [lost, setLost] = useState(false);
  const clientRef = useRef<NotesClient | null>(null);

  useEffect(() => {
    const client = createNotesClient({
      onState: (s) => { setState(s); setLost(false); },
      onEnded: () => window.close(),
      onDisconnected: () => setLost(true),
    });
    clientRef.current = client;

    // The shell also reports the closed window to the host, so this is the fast path rather than the
    // only one - but it lets the inline popover come back the moment the window goes.
    const bye = () => client.close();
    window.addEventListener("pagehide", bye);
    return () => { window.removeEventListener("pagehide", bye); client.dispose(); };
  }, []);

  const client = clientRef.current;
  const live = state !== null && !lost;

  return (
    <div style={{ padding: 16, display: "flex", flexDirection: "column", gap: 12, height: "100vh", boxSizing: "border-box" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <span
          aria-hidden
          style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--hub-red)", animation: "blink 1.2s infinite" }}
        />
        <span style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 17, color: "var(--hub-text)" }}>
          {t("liveNotesTitle")}
        </span>
      </div>

      {state === null && (
        <p style={{ margin: 0, fontSize: 13, color: "var(--hub-muted)" }}>{t("notesPopoutWaiting")}</p>
      )}

      {lost && (
        <p role="status" style={{ margin: 0, fontSize: 13, color: "var(--hub-red-text)" }}>
          {t("notesPopoutDisconnected")}
        </p>
      )}

      {state !== null && (
        <div style={{ flex: 1, overflowY: "auto" }}>
          <NotesSection
            notes={state.lines}
            // Omitting a handler hides its control, which is how NotesSection already renders
            // read-only. A disconnected pop-out must not accept a line it cannot deliver.
            onAdd={live ? (text) => client?.add(text) : undefined}
            onEdit={live ? (id, text) => client?.edit(id, text) : undefined}
            onDelete={live ? (id) => client?.remove(id) : undefined}
          />
        </div>
      )}

      {state?.canCapture && (
        <div style={{ borderTop: "1px solid var(--hub-border)", paddingTop: 8, display: "flex", flexDirection: "column", gap: 6 }}>
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
            <span style={{ fontSize: 12, fontWeight: 600, color: "var(--hub-text-2)" }}>
              {t("screenshots")} ({state.shots.length})
            </span>
            <div style={{ display: "flex", gap: 6 }}>
              <button
                type="button"
                onClick={() => client?.capture()}
                disabled={!live || !state.captureAreaSet}
                title={state.captureAreaSet ? undefined : t("screenshotCaptureNeedsArea")}
                style={{ fontSize: 12, padding: "2px 6px", borderRadius: 6, border: "1px solid var(--hub-border)", background: "transparent", color: "var(--hub-text-2)" }}
              >
                {t("screenshotCaptureButton")}
              </button>
              <button
                type="button"
                onClick={() => client?.changeArea()}
                disabled={!live}
                style={{ fontSize: 12, padding: "2px 6px", borderRadius: 6, border: "1px solid var(--hub-border)", background: "transparent", color: "var(--hub-text-2)" }}
              >
                {t("screenshotCaptureArea")}
              </button>
            </div>
          </div>
          <ShotStrip shots={state.shots} onDelete={(id) => client?.removeShot(id)} />
        </div>
      )}
    </div>
  );
}
```

`NotesSection`'s input has no `disabled` prop today. Add one so the disconnected state can be honoured:
in `apps/web/src/components/NotesSection.tsx`, add `disabled?: boolean` to the props and put
`disabled={disabled}` on the add `<input>` and the Add `<button>`. Passing `onAdd={undefined}` alone
hides the box entirely, which would make the notes look lost rather than paused - so `NotesPopout`
should pass `onAdd` **and** `disabled={!live}` instead. Adjust the page accordingly:

```tsx
            onAdd={(text) => client?.add(text)}
            disabled={!live}
```

- [ ] **Step 5: Add the route**

In `apps/web/src/App.tsx`, add alongside the other top-level public routes (near the `/help` routes,
around line 66) - **outside** the `/` route so the workspace layout, the recorder and SignalR never
mount here:

```tsx
      {/* The desktop shell's detached notes window. Deliberately outside the workspace layout and
          outside RequireAuth: it holds no server data, renders nothing until the main window answers
          on the same-origin channel, and a login redirect inside a 380px window would be nonsense. */}
      <Route path="/notes-popout" element={<NotesPopout />} />
```

Add the import at the top: `import NotesPopout from "./pages/NotesPopout";`

- [ ] **Step 6: Run the tests and verify they pass**

```bash
cd apps/web && npx vitest run src/pages/NotesPopout.test.tsx src/components/NotesSection.test.tsx
```

Expected: PASS.

- [ ] **Step 7: Typecheck and run the full suite**

```bash
cd apps/web && npm run build && npm test
```

Expected: both clean.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/pages/NotesPopout.tsx apps/web/src/pages/NotesPopout.test.tsx apps/web/src/App.tsx apps/web/src/components/NotesSection.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(notes): add the pop-out notes page and route"
```

---

### Task 4: Host-side lifecycle hook

**Files:**
- Create: `apps/web/src/lib/useNotesPopout.ts`
- Create: `apps/web/src/lib/useNotesPopout.test.tsx`

**Interfaces:**
- Consumes: `createNotesHost`, `NotesHost`, `NotesHostHandlers`, `NotesState` (Task 2).
- Produces: `useNotesPopout(args) -> { poppedOut: boolean; popOut: () => void }`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/useNotesPopout.test.tsx`:

```tsx
import { render, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { useNotesPopout } from "./useNotesPopout";
import type { NotesState, NotesHostHandlers } from "./notesChannel";

const host = { publish: vi.fn(), end: vi.fn(), dispose: vi.fn() };
let captured: NotesHostHandlers;

vi.mock("./notesChannel", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./notesChannel")>()),
  createNotesHost: (h: NotesHostHandlers) => { captured = h; return host; },
}));

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [], shots: [], canCapture: false, captureAreaSet: false, recording: true, ...over,
});

function Harness({ state: s, openWindow, onAdd }: { state: NotesState; openWindow: () => void; onAdd?: (t: string) => void }) {
  const { poppedOut, popOut } = useNotesPopout({
    state: s,
    openWindow,
    handlers: {
      onAdd: onAdd ?? vi.fn(), onEdit: vi.fn(), onDelete: vi.fn(), onDeleteShot: vi.fn(),
      onCapture: vi.fn(), onChangeArea: vi.fn(),
    },
  });
  return <button onClick={popOut}>{poppedOut ? "out" : "in"}</button>;
}

beforeEach(() => vi.clearAllMocks());

describe("useNotesPopout", () => {
  it("does not open a channel until the user pops out", () => {
    render(<Harness state={state()} openWindow={vi.fn()} />);
    expect(host.publish).not.toHaveBeenCalled();
  });

  it("opens the shell window and starts hosting on popOut", () => {
    const openWindow = vi.fn();
    const { getByRole } = render(<Harness state={state()} openWindow={openWindow} />);
    act(() => getByRole("button").click());
    expect(openWindow).toHaveBeenCalledTimes(1);
    expect(getByRole("button").textContent).toBe("out");
  });

  it("republishes when the state changes", () => {
    const { getByRole, rerender } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    const before = host.publish.mock.calls.length;
    rerender(<Harness state={state({ captureAreaSet: true })} openWindow={vi.fn()} />);
    expect(host.publish.mock.calls.length).toBeGreaterThan(before);
  });

  it("ends the session and comes back inline when the recording stops", () => {
    const { getByRole, rerender } = render(<Harness state={state({ recording: true })} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    rerender(<Harness state={state({ recording: false })} openWindow={vi.fn()} />);
    expect(host.end).toHaveBeenCalledTimes(1);
    expect(getByRole("button").textContent).toBe("in");
  });

  it("comes back inline when the pop-out window closes", () => {
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    act(() => captured.onClientClosed());
    expect(getByRole("button").textContent).toBe("in");
  });

  it("survives the close being reported twice", () => {
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    act(() => { captured.onClientClosed(); captured.onClientClosed(); });
    expect(getByRole("button").textContent).toBe("in");
  });

  // The shell reports the closed window over IPC, which is the only signal that survives the pop-out's
  // renderer being killed outright - its own "closing" message would never be sent.
  it("comes back inline when the shell reports the window gone", () => {
    let notify: () => void = () => {};
    function ShellHarness() {
      const { poppedOut, popOut, notifyClosed } = useNotesPopout({
        state: state(),
        openWindow: vi.fn(),
        handlers: {
          onAdd: vi.fn(), onEdit: vi.fn(), onDelete: vi.fn(), onDeleteShot: vi.fn(),
          onCapture: vi.fn(), onChangeArea: vi.fn(),
        },
      });
      notify = notifyClosed;
      return <button onClick={popOut}>{poppedOut ? "out" : "in"}</button>;
    }
    const { getByRole } = render(<ShellHarness />);
    act(() => getByRole("button").click());
    expect(getByRole("button").textContent).toBe("out");
    act(() => notify());
    expect(getByRole("button").textContent).toBe("in");
  });
});
```

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/lib/useNotesPopout.test.tsx
```

Expected: FAIL - cannot resolve `./useNotesPopout`.

- [ ] **Step 3: Write the hook**

Create `apps/web/src/lib/useNotesPopout.ts`:

```ts
import { useEffect, useRef, useState } from "react";
import { createNotesHost, type NotesHost, type NotesHostHandlers, type NotesState } from "./notesChannel";

/// Everything the host answers except `getState` and `onClientClosed`, which this hook supplies.
export type NotesPopoutHandlers = Omit<NotesHostHandlers, "getState" | "onClientClosed">;

/**
 * The main window's half of the pop-out. Owns whether the notes are detached, the channel host's
 * lifetime, and republishing whenever the state changes.
 *
 * Nothing is created until the user actually pops out: an idle BroadcastChannel in every session would
 * be pure cost, and the pop-out is opt-in per recording by design.
 */
export function useNotesPopout({
  state,
  handlers,
  openWindow,
}: {
  state: NotesState;
  handlers: NotesPopoutHandlers;
  /// Asks the shell to open the window. Absent capability is the caller's problem - it should not
  /// offer the control at all in a plain browser.
  openWindow: () => void;
}): { poppedOut: boolean; popOut: () => void; notifyClosed: () => void } {
  const [poppedOut, setPoppedOut] = useState(false);
  const hostRef = useRef<NotesHost | null>(null);

  // The host reads state through a ref so its getState always sees the current value without the
  // channel having to be torn down and rebuilt on every keystroke.
  const stateRef = useRef(state);
  stateRef.current = state;
  const handlersRef = useRef(handlers);
  handlersRef.current = handlers;

  useEffect(() => {
    if (!poppedOut) return;
    const host = createNotesHost({
      getState: () => stateRef.current,
      onAdd: (text) => handlersRef.current.onAdd(text),
      onEdit: (id, text) => handlersRef.current.onEdit(id, text),
      onDelete: (id) => handlersRef.current.onDelete(id),
      onDeleteShot: (id) => handlersRef.current.onDeleteShot(id),
      onCapture: () => handlersRef.current.onCapture(),
      onChangeArea: () => handlersRef.current.onChangeArea(),
      // Idempotent on purpose: this arrives from the client's own `closing` message AND from the
      // shell noticing the window was destroyed, and either may be first or missing.
      onClientClosed: () => setPoppedOut(false),
    });
    hostRef.current = host;
    host.publish();
    return () => { host.dispose(); hostRef.current = null; };
  }, [poppedOut]);

  // Republish on any change the pop-out renders.
  useEffect(() => {
    if (poppedOut) hostRef.current?.publish();
  }, [poppedOut, state]);

  // The recording is what the pop-out exists for; when it ends, so does the window.
  useEffect(() => {
    if (poppedOut && !state.recording) {
      hostRef.current?.end();
      setPoppedOut(false);
    }
  }, [poppedOut, state.recording]);

  return {
    poppedOut,
    popOut: () => { openWindow(); setPoppedOut(true); },
    /// Report the pop-out window gone from outside the channel - the shell noticing it closed. Needed
    /// because a killed renderer never sends its own `closing` message. Same idempotent path.
    notifyClosed: () => setPoppedOut(false),
  };
}
```

- [ ] **Step 4: Run the test and verify it passes**

```bash
cd apps/web && npx vitest run src/lib/useNotesPopout.test.tsx
```

Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/useNotesPopout.ts apps/web/src/lib/useNotesPopout.test.tsx
git commit -m "feat(notes): add the host-side pop-out lifecycle hook"
```

---

### Task 5: The shell's pure window model

**Files:**
- Create: `apps/desktop/src/notesWindowState.js`
- Create: `apps/desktop/src/notesWindowState.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `notesWindowBounds(saved, displays)` returning `{ width, height }` or
  `{ x, y, width, height }`; `MIN_NOTES_SIZE` = `{ width: 300, height: 360 }`.

- [ ] **Step 1: Write the failing test**

Create `apps/desktop/src/notesWindowState.test.js`:

```js
"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { notesWindowBounds, MIN_NOTES_SIZE } = require("./notesWindowState");

const laptop = { bounds: { x: 0, y: 0, width: 1536, height: 864 } };
const second = { bounds: { x: 1536, y: 0, width: 1920, height: 1080 } };

test("with nothing remembered, uses the default size and lets the OS place it", () => {
  assert.deepEqual(notesWindowBounds(undefined, [laptop]), { width: 380, height: 520 });
});

test("remembered bounds on an attached display are reused", () => {
  const saved = { x: 200, y: 140, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), saved);
});

test("remembered bounds on a display that is gone fall back to the default", () => {
  const saved = { x: 2000, y: 300, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), { width: 380, height: 520 });
});

test("remembered bounds on a second display are kept while it is attached", () => {
  const saved = { x: 2000, y: 300, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop, second]), saved);
});

test("a remembered size too small to use is raised to the minimum", () => {
  const saved = { x: 100, y: 100, width: 40, height: 30 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), {
    x: 100, y: 100, width: MIN_NOTES_SIZE.width, height: MIN_NOTES_SIZE.height,
  });
});

test("garbage in the store does not propagate", () => {
  assert.deepEqual(notesWindowBounds({ x: "left", y: null }, [laptop]), { width: 380, height: 520 });
});
```

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/desktop && node --test src/notesWindowState.test.js
```

Expected: FAIL - cannot find module `./notesWindowState`.

- [ ] **Step 3: Write the model**

Create `apps/desktop/src/notesWindowState.js`:

```js
"use strict";

// Pure model for the pop-out notes window's geometry. main.js owns the BrowserWindow; the decisions
// live here so they can be unit-tested without Electron, matching recorderState.js/updateState.js.

const DEFAULT_SIZE = { width: 380, height: 520 };
const MIN_NOTES_SIZE = { width: 300, height: 360 };

function isFinitePoint(saved) {
  return (
    saved &&
    Number.isFinite(saved.x) && Number.isFinite(saved.y) &&
    Number.isFinite(saved.width) && Number.isFinite(saved.height)
  );
}

/// Whether the window's top-left corner still lands on an attached display. Checked against the
/// corner rather than the whole rectangle so a window hanging slightly off an edge is still
/// restored - the case that matters is the monitor being gone entirely, which would otherwise put
/// the notes window somewhere the user cannot see or reach it.
function onSomeDisplay(saved, displays) {
  return (displays || []).some((d) => {
    const b = d && d.bounds;
    if (!b) return false;
    return saved.x >= b.x && saved.x < b.x + b.width && saved.y >= b.y && saved.y < b.y + b.height;
  });
}

/// Bounds for the notes window: the remembered ones when they still make sense, else a default size
/// with placement left to the OS (hence no x/y in that result).
function notesWindowBounds(saved, displays) {
  if (!isFinitePoint(saved) || !onSomeDisplay(saved, displays)) return { ...DEFAULT_SIZE };
  return {
    x: saved.x,
    y: saved.y,
    width: Math.max(saved.width, MIN_NOTES_SIZE.width),
    height: Math.max(saved.height, MIN_NOTES_SIZE.height),
  };
}

module.exports = { notesWindowBounds, DEFAULT_SIZE, MIN_NOTES_SIZE };
```

- [ ] **Step 4: Run the test and verify it passes**

```bash
cd apps/desktop && node --test src/notesWindowState.test.js
```

Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/notesWindowState.js apps/desktop/src/notesWindowState.test.js
git commit -m "feat(desktop): add the notes-window geometry model"
```

---

### Task 6: The shell window, preload and IPC

`main.js` is not unit-tested here (it needs a running Electron); the testable decisions were extracted
in Task 5. This task ends with a manual verification instead, which is stated explicitly rather than
skipped.

**Files:**
- Create: `apps/desktop/src/notes-preload.js`
- Modify: `apps/desktop/src/main.js`
- Modify: `apps/desktop/src/preload.js`

**Interfaces:**
- Consumes: `notesWindowBounds`, `MIN_NOTES_SIZE` (Task 5).
- Produces: on the main window's `window.diariz` - `openNotesPopout(): Promise<{ ok: boolean }>` and
  `onNotesPopoutClosed(cb): () => void`. On the pop-out's `window.diarizNotes` - `{ isPopout: true }`.

- [ ] **Step 1: Write the pop-out preload**

Create `apps/desktop/src/notes-preload.js`:

```js
"use strict";

const { contextBridge } = require("electron");

// The pop-out window's entire bridge. Deliberately NOT preload.js: that one exposes onTrayCommand,
// and a second subscriber would mean a tray "stop" driving two recorders. The pop-out needs none of
// it - it talks to the main window over a same-origin BroadcastChannel, not over IPC, and even its
// screenshot buttons are relayed through the host rather than invoked here.
contextBridge.exposeInMainWorld("diarizNotes", {
  isPopout: true,
});
```

- [ ] **Step 2: Add the main-window bridge methods**

In `apps/desktop/src/preload.js`, inside the existing `exposeInMainWorld("diariz", { ... })` object,
add:

```js
  /// Ask the shell to open the detached live-notes window. Resolves { ok } - false when no server
  /// address is configured yet.
  openNotesPopout: () => ipcRenderer.invoke("notes:open"),

  /// Subscribe to the pop-out window being closed, however it was closed (its own button, the OS, a
  /// crash). The web app restores the inline notes popover on this. Returns an unsubscribe function.
  onNotesPopoutClosed: (cb) => {
    const listener = () => cb();
    ipcRenderer.on("notes:closed", listener);
    return () => ipcRenderer.removeListener("notes:closed", listener);
  },
```

- [ ] **Step 3: Add the window lifecycle to `main.js`**

Add to the requires at the top of `apps/desktop/src/main.js` (it already imports `screen` via
`electron`; add it to that destructure if missing):

```js
const { notesWindowBounds } = require("./notesWindowState");
```

Add beside the other window handles (near line 65):

```js
let notesWindow = null;
```

Add this section after `reloadMainWindow()` (around line 180):

```js
// ---- Pop-out live-notes window ----
//
// A second window on the SAME origin as the main window, which is what lets the two halves of the
// notes UI talk over a BroadcastChannel with no IPC of their own. It is always-on-top at Electron's
// DEFAULT level: a spike confirmed that survives another app going full screen, and the higher
// "screen-saver" band would also float over the lock screen, which this has no business doing.

function showNotesPopout() {
  const url = targetUrl();
  if (!url) return { ok: false };

  if (notesWindow && !notesWindow.isDestroyed()) {
    notesWindow.show();
    notesWindow.focus();
    return { ok: true };
  }

  const bounds = notesWindowBounds(store.get("notesPopout.bounds"), screen.getAllDisplays());
  notesWindow = new BrowserWindow({
    ...bounds,
    alwaysOnTop: true,
    title: "Diariz - Notes",
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "notes-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  notesWindow.setMenuBarVisibility(false);

  const origin = new URL(url).origin;
  notesWindow.webContents.setWindowOpenHandler(({ url: target }) => {
    shell.openExternal(target);
    return { action: "deny" };
  });
  notesWindow.webContents.on("will-navigate", (e, target) => {
    if (new URL(target).origin !== origin) {
      e.preventDefault();
      shell.openExternal(target);
    }
  });

  // Remembered on close rather than on move/resize: the bounds only matter for the next open, and
  // writing the store on every drag frame would be pure churn.
  notesWindow.on("close", () => {
    if (notesWindow && !notesWindow.isDestroyed()) store.set("notesPopout.bounds", notesWindow.getBounds());
  });
  notesWindow.on("closed", () => {
    notesWindow = null;
    // The guaranteed path back to the inline popover. The renderer also sends its own "closing" over
    // the channel, but that cannot be relied on if the renderer died.
    if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send("notes:closed");
  });

  notesWindow.loadURL(new URL("/notes-popout", url).toString(), documentLoadOptions());
  return { ok: true };
}

function closeNotesPopout() {
  if (notesWindow && !notesWindow.isDestroyed()) notesWindow.close();
}

ipcMain.handle("notes:open", () => showNotesPopout());
```

- [ ] **Step 4: Tear the window down with the app**

In `createMainWindow`'s `closed` handler (around line 135), add `closeNotesPopout();` before
`setRecorderReady(false);` - a pop-out outliving the window that feeds it can only sit there dead.

Find where the app sets `isQuitting = true` (the tray Quit item and/or a `before-quit` handler) and add
`closeNotesPopout();` there too, so the bounds are saved on the way out.

- [ ] **Step 5: Manually verify (this cannot be unit-tested)**

Run the shell against the dev server:

```bash
cd apps/web && npm run dev
```

Then in a second terminal:

```bash
cd apps/desktop && npm run dev
```

Confirm all of:
1. Start a recording, open the notes popover - the pop-out control appears (it will not until Task 7;
   until then, trigger the window from the devtools console with
   `await window.diariz.openNotesPopout()`).
2. The window opens at `/notes-popout`, shows the "Waiting..." state (no host until Task 7), and
   **floats above another app put into full screen**.
3. Close it, reopen it, and confirm it comes back at the size and position you left it.
4. Move it to a second display if you have one, close, detach the display, reopen - it must come back
   on the remaining display, not off-screen.

- [ ] **Step 6: Commit**

```bash
git add apps/desktop/src/notes-preload.js apps/desktop/src/main.js apps/desktop/src/preload.js
git commit -m "feat(desktop): open the pop-out notes window from the shell"
```

---

### Task 7: Wire the host into the recorder

The last connection: the pop-out button on the popover, and the `Recorder` supplying state and
handlers to `useNotesPopout`.

**Files:**
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`
- Modify: `apps/web/src/components/hub/NotesPopover.test.tsx`
- Modify: `apps/web/src/components/Recorder.tsx`
- Modify: `apps/web/src/components/Recorder.test.tsx`
- Modify: `apps/web/src/lib/trayRecorder.ts` (the `TrayBridge` interface)

**Interfaces:**
- Consumes: `useNotesPopout` (Task 4), `NotesState` (Task 2), `ShotView` (Task 1).
- Produces: nothing further.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/components/hub/NotesPopover.test.tsx`:

The file already defines `baseProps` and a `renderPopover(overrides)` helper at the top - use them:

```tsx
describe("NotesPopover pop-out control", () => {
  it("offers no pop-out control in a plain browser", () => {
    renderPopover();
    expect(screen.queryByRole("button", { name: /separate window/i })).toBeNull();
  });

  it("pops out when the shell supports it", () => {
    const onPopOut = vi.fn();
    renderPopover({ onPopOut });
    fireEvent.click(screen.getByRole("button", { name: /separate window/i }));
    expect(onPopOut).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
cd apps/web && npx vitest run src/components/hub/NotesPopover.test.tsx
```

Expected: FAIL - no button matching `/separate window/i`.

- [ ] **Step 3: Add the control to `NotesPopover`**

Add to `NotesPopoverProps`:

```tsx
  /// Detach the notes into their own always-on-top window. Absent in a plain browser, which is what
  /// hides the control - only the desktop shell can pin a window over a full-screen call.
  onPopOut?: () => void;
```

Add the icon beside the existing `IconClose` definition:

```tsx
const IconPopOut = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M14 4h6v6M20 4l-8 8M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
  </svg>
);
```

In the header row, immediately **before** the close button, add:

```tsx
          {onPopOut && (
            <button
              type="button"
              aria-label={t("notesPopOut")}
              title={t("notesPopOut")}
              onClick={onPopOut}
              style={{
                marginLeft: "auto", display: "flex", alignItems: "center", justifyContent: "center",
                width: 28, height: 28, borderRadius: 8, border: "none", background: "transparent",
                color: "var(--hub-muted)", cursor: "pointer",
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = "var(--hub-surface-hover)")}
              onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
            >
              <IconPopOut />
            </button>
          )}
```

The close button currently carries `marginLeft: "auto"`. Move that to whichever control comes first so
the pair stays right-aligned: when `onPopOut` is present the pop-out button takes it, so change the
close button's style to `marginLeft: onPopOut ? 0 : "auto"`.

- [ ] **Step 4: Run the tests and verify they pass**

```bash
cd apps/web && npx vitest run src/components/hub/NotesPopover.test.tsx
```

Expected: PASS.

- [ ] **Step 5: Write the failing Recorder test**

Add to `apps/web/src/components/Recorder.test.tsx`:

Add a new `describe` block, following the shape of the existing "in-app capture button" block (around
line 1292) - same shell install/teardown, same way of starting a recording:

```tsx
describe("notes pop-out", () => {
  function installShellWithPopout() {
    const openNotesPopout = vi.fn().mockResolvedValue({ ok: true });
    (window as unknown as { diariz?: unknown }).diariz = {
      openNotesPopout,
      onNotesPopoutClosed: () => () => {},
    };
    return openNotesPopout;
  }

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  afterEach(() => {
    delete (window as unknown as { diariz?: unknown }).diariz;
  });

  it("offers no pop-out control in a plain browser", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    expect(screen.queryByRole("button", { name: /separate window/i })).toBeNull();
  });

  it("popping out closes the inline popover and asks the shell for a window", async () => {
    const openNotesPopout = installShellWithPopout();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    // The notes popover auto-opens at record start unless the preference says otherwise.
    fireEvent.click(screen.getByRole("button", { name: /separate window/i }));

    expect(openNotesPopout).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("notes-popover")).toBeNull();
  });
});
```

If the popover does not auto-open in this harness, click the Notes toggle first:
`fireEvent.click(screen.getByRole("button", { name: /^notes$/i }));`

- [ ] **Step 6: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/components/Recorder.test.tsx -t "popping out"
```

Expected: FAIL - no button matching `/separate window/i`.

- [ ] **Step 7: Declare the new shell methods**

In `apps/web/src/lib/trayRecorder.ts`, add to the `TrayBridge` interface:

```ts
  openNotesPopout?: () => Promise<{ ok: boolean }>;
  onNotesPopoutClosed?: (cb: () => void) => () => void;
```

- [ ] **Step 8: Wire the host in `Recorder.tsx`**

Add the imports:

```tsx
import { useNotesPopout } from "../lib/useNotesPopout";
import type { NotesState } from "../lib/notesChannel";
```

After the live-notes handlers (after `deleteLiveNote`, around line 685), add:

```tsx
  // ---- Pop-out notes window (desktop shell only) ----

  const shellBridge = (window as { diariz?: { openNotesPopout?: () => Promise<{ ok: boolean }>; onNotesPopoutClosed?: (cb: () => void) => () => void } }).diariz;

  // Rebuilt on every render the pop-out cares about; useNotesPopout republishes when it changes.
  const notesState: NotesState = {
    lines: liveLines,
    // Only what the pop-out renders. The full-resolution PNG stays here.
    shots: liveShots.map((s) => ({ id: s.id, capturedAtMs: s.capturedAtMs, thumb: s.thumb })),
    canCapture: canCaptureScreenshots(),
    captureAreaSet,
    recording,
  };

  const { poppedOut, popOut, notifyClosed } = useNotesPopout({
    state: notesState,
    openWindow: () => void shellBridge?.openNotesPopout?.(),
    handlers: {
      onAdd: addLiveNote,
      onEdit: editLiveNote,
      onDelete: deleteLiveNote,
      onDeleteShot: deleteLiveShot,
      onCapture: requestCapture,
      onChangeArea: requestChangeArea,
    },
  });

  // Popping out moves the notes into the window, so the inline popover closes. The preference is not
  // touched: it records whether the popover auto-opens on the next recording, which is a different
  // question from where the notes are right now.
  useEffect(() => {
    if (poppedOut && hub.isOpen("notes")) hub.close();
  }, [poppedOut]);
```

The shell's "window was closed" signal also has to reach the hook: `useNotesPopout` restores itself via
`onClientClosed`, and the client sends `closing` on `pagehide`, but a killed renderer sends nothing. `useNotesPopout` already returns
`notifyClosed` for exactly this (Task 4), so destructure it and hand it to the subscription:

```tsx
  const { poppedOut, popOut, notifyClosed } = useNotesPopout({ /* ...as above... */ });

  // The shell's report is the guaranteed one - it survives the pop-out's renderer being killed, which
  // the channel's own "closing" message does not. The channel message is merely faster.
  useEffect(() => {
    if (!poppedOut || !shellBridge?.onNotesPopoutClosed) return;
    return shellBridge.onNotesPopoutClosed(() => { hub.close(); notifyClosed(); });
  }, [poppedOut, notifyClosed]);
```

Replace the earlier `onNotesPopoutClosed` effect with this one - there should be exactly one.

Finally, pass the control to the popover - only when the shell can actually open a window:

```tsx
              onPopOut={shellBridge?.openNotesPopout ? popOut : undefined}
```

- [ ] **Step 9: Run the tests and verify they pass**

```bash
cd apps/web && npx vitest run src/components/Recorder.test.tsx src/lib/useNotesPopout.test.tsx
```

Expected: PASS.

- [ ] **Step 10: Typecheck and run the full suite**

```bash
cd apps/web && npm run build && npm test
```

Expected: both clean.

- [ ] **Step 11: End-to-end manual verification**

With `npm run dev` (web) and `npm run dev` (desktop) both running, and signed in:

1. Start a recording. Open the notes popover. Click the pop-out control.
2. The popover closes and the floating window appears showing the same notes.
3. Type a line in the pop-out - it appears there **with an mm:ss stamp**. That stamp proves the host
   applied it; the pop-out cannot produce one.
4. Pause the recording, wait 10 seconds, resume, add another line. Its stamp must skip the paused
   time.
5. Close the main window to the tray. Add another line in the pop-out. It must still work.
6. Bring the main window back, reopen the notes popover - every line from the pop-out is there.
7. Stop the recording. The pop-out window closes by itself, and the notes are attached to the
   recording.
8. Reload the main window (Ctrl+R) while popped out. Within ~6 seconds the pop-out shows the
   disconnected message and its input goes dead.
9. Close the pop-out by its own window button - the inline popover comes back.

- [ ] **Step 12: Commit**

```bash
git add apps/web/src/components/Recorder.tsx apps/web/src/components/Recorder.test.tsx apps/web/src/components/hub/NotesPopover.tsx apps/web/src/components/hub/NotesPopover.test.tsx apps/web/src/lib/trayRecorder.ts apps/web/src/lib/useNotesPopout.ts
git commit -m "feat(notes): pop the live notes out into a floating window"
```

---

### Task 8: Release checklist and documentation

Per CLAUDE.md every PR ships exactly one release. This is a functional enhancement, so Minor +1.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/` - the article covering notes during a call

- [ ] **Step 1: Bump the version in all five places**

`0.210.1` -> `0.211.0` in:
- `version.json` (canonical)
- `apps/web/package.json`
- `apps/desktop/package.json`
- `src/Diariz.Api/Diariz.Api.csproj` (the `<Version>` element)
- `integrations/n8n-nodes-diariz/package.json`

- [ ] **Step 2: Run the mirror test and watch it agree**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

`releases.test.ts` will FAIL until Step 3 - that is expected and is the point of the assertion.

- [ ] **Step 3: Add the release entry**

At the top of `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.211.0",
    date: "2026-08-13",
    pr: 0, // replace with the real PR number - see Step 7
    headline: "Take notes in a small floating window while your call has the screen",
    summary:
      "On one monitor, taking notes during a call meant keeping the whole of Diariz visible - which " +
      "is exactly the screen the call wants. While a recording is running, the notes panel now has a " +
      "control that moves it into its own small window that floats above everything else, including " +
      "a full-screen Teams or Zoom call. It is the same notes panel, with the same timestamps and " +
      "the same screenshot buttons, and you can close the main Diariz window to the tray and keep " +
      "typing. Stop the recording and the window closes itself, with your notes attached to the " +
      "recording as usual. Desktop app only - a browser cannot float a window above another " +
      "application.",
    added: [
      "A control on the notes panel opens your live notes in a small always-on-top window while recording.",
      "The floating notes window keeps working with the main Diariz window closed to the tray.",
    ],
    changed: [
      "Screenshots taken during a recording are now deleted by identity rather than by position, so deleting one while another arrives removes the one you clicked.",
    ],
  },
```

- [ ] **Step 4: Update the About-box capability row**

In the `CAPABILITIES` table in the same file, extend the existing **Notes** row - do not add a new row:

> ...they appear inline in the transcript at the moment you wrote them, steer the minutes, and can be
> woven into an enhanced-notes section linking to the exact transcript moments. In the desktop app, pop
> the notes out into a small always-on-top window that floats over a full-screen call, so a single
> monitor is no longer a reason to lose sight of the meeting.

- [ ] **Step 5: Update the README and `docs/features.md` together**

Both must move in lockstep - one without the other is the documented failure mode.

In `README.md`'s Features table, extend the Notes row with the same one-line summary. In
`docs/features.md`, extend the Notes bullet with the prose version.

- [ ] **Step 6: Update the architecture doc and the help article**

`docs/Overall_Synopsis_of_Platform.md` gains a short subsection under the desktop shell: a second
`BrowserWindow` at `/notes-popout` on the same origin, its own narrow preload, and the
`BroadcastChannel` contract between it and the main window (host owns state; client sends text; client
drives the liveness poll because a hidden host's timers are throttled). This is a new cross-boundary
contract, so it belongs there.

`docs/Data_Schema.md` needs **no** change - nothing about storage moved.

Find the help article covering live notes:

```bash
grep -rl "notes" apps/web/src/content/help/
```

Add a short section on popping the notes out, what happens when the recording stops, and that it is
desktop-only. **ASCII only**, and keep the front-matter `summary` to two or three sentences.

- [ ] **Step 7: Push, open the PR, then correct the PR number**

The `pr:` field needs a number that does not exist until the PR does, and guessing "last + 1" fails
because Dependabot and issues share the sequence. So: push, open the PR, read the number it was given,
put it in `releases.ts`, and push again.

```bash
git push -u origin feat/notes-popout-window
gh pr create --title "Pop the live notes out into a floating window" --body "..."
```

The PR body must state the deployment surface: **this needs both a desktop release (a `v*` tag) and a
server redeploy**, because it touches `apps/desktop/src/**` and `apps/web`. Note that older installed
desktop apps pick up the web half and simply never show the control, which degrades correctly.

- [ ] **Step 8: Verify the whole suite before asking for review**

```bash
cd apps/web && npm run build && npm test
cd ../desktop && npm test
cd ../.. && dotnet build Diariz.slnx
```

Building the solution catches integration-test and CodeQL compile breaks a unit-only run misses.

- [ ] **Step 9: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help
git commit -m "chore: release 0.211.0"
```

---

## Where this plan departs from the spec

Two deliberate changes, both flagged rather than made quietly. Raise them if you disagree before
building on them.

**1. `useLiveNotes()` is NOT extracted from `Recorder.tsx`.** The spec called for it, for two reasons:
to make the new logic testable outside the recorder, and to stop a 1526-line file growing further. The
first is met a better way here - the channel host lives in `useNotesPopout`, its own tested module, so
none of the new logic sits in `Recorder.tsx` at all. That leaves only the second reason, and the
recorder gains about 30 lines of wiring. Moving `liveLines`/`mirrorLines`/add/edit/delete purely to
shrink a file would be a refactor with no test to justify it and real regression surface around the
durability path. If you want the extraction, do it as its own PR where it can be reviewed on its own
merits.

**2. The spec's "BroadcastChannel unavailable" fallback is subsumed.** The spec said to feature-detect
`BroadcastChannel` and hide the control if absent. The control is instead gated on
`window.diariz.openNotesPopout`, which only the desktop shell provides - and Electron 43 always has
`BroadcastChannel`. A separate check would be unreachable code. If the pop-out is ever offered in a
plain browser, that check comes back.

## Notes for the implementer

**Two things in this plan look wrong and are not.** Both come from measurements recorded in the spec:

1. **The pop-out polls the host, not the other way round.** A hidden main window has its timers
   throttled (measured at ~1 Hz, and far worse past five minutes hidden), so a host heartbeat would
   stall exactly when the user has closed the app to the tray - the scenario this feature is for.
2. **The pin level is Electron's default, not `"screen-saver"`.** The default was verified to survive
   another app going full screen, so the higher band buys nothing and would also float the notes over
   the lock screen.

**If the manual verification in Task 7 Step 4 fails** (a stamp that does not skip paused time), the
cause is the pop-out having acquired a clock of its own. It must never compute `capturedAtMs`.

**Out of scope, noticed during the spikes:** `Recorder.tsx` runs its display ticker and its auto-stop
watcher on `setInterval`, and both are throttled while the app sits in the tray. The display
self-corrects and the auto-stop compares wall-clock time so it will not drift, but it could *notice*
late. Do not fix it here.
