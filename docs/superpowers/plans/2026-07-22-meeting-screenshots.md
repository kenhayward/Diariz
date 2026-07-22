# Meeting Screenshots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user capture screenshots during a recording from the Windows desktop client, and have each capture appear in the transcript at the moment it was taken, with thumbnails, a modal viewer, and a collapsed list in the Notes tab.

**Architecture:** The Electron main process owns capture (hotkey, tray, picker overlay, per-recording capture target, grab/crop/resize) and pushes image bytes to the renderer. The renderer stamps the pause-aware recording clock, stashes bytes in IndexedDB, and uploads them to a new `MeetingScreenshot` API once the recording row exists. The transcript weaves screenshots in by captured time using the existing note-anchoring rule.

**Tech Stack:** Electron 43 (`desktopCapturer`, `nativeImage`, `globalShortcut`, `electron-store`), ASP.NET Core 10 + EF Core + Postgres + MinIO, React 19 + TypeScript + Tailwind v4, vitest + `@testing-library/react`, xUnit + Testcontainers, `node --test`.

**Design spec:** `docs/superpowers/specs/2026-07-22-meeting-screenshots-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail, then write the minimal code to pass. No production code without a preceding failing test.
- **Keep test output pristine** - a passing run has no errors or warnings.
- **No em or en dashes in user-facing text.** Use a plain hyphen `-` in all UI strings, i18n catalogs, and release notes. Code and internal docs are unaffected.
- **Never commit or push to `main`.** All work lands on branch `feat/meeting-screenshots` and ships as a PR.
- **Ownership checks:** every recording-scoped API endpoint filters by `UserId` from the JWT `NameIdentifier` claim.
- **Postgres-only EF model config** goes behind the `Database.IsNpgsql()` guard in `OnModelCreating`. `MeetingScreenshot` uses plain columns only, so it stays outside that guard (like `RecordingTag`).
- **Build `Diariz.slnx`, not just the unit test project,** before pushing - unit-only runs miss integration and CodeQL compile breaks.
- **Storage keys:** full image `{userId}/screenshots/{id}.png`, thumbnail `{userId}/screenshots/{id}.thumb.jpg`.
- **Image formats:** full = PNG, long edge capped at 2560; thumbnail = JPEG, long edge 320.
- **Default hotkey:** `CommandOrControl+Shift+9`.
- **Desktop-only feature:** all web affordances are hidden when `window.diariz` is absent.

---

## File Structure

**Desktop (`apps/desktop/src/`)**

| File | Responsibility |
|---|---|
| `captureTarget.js` (create) | Pure geometry: crop rect from a display + selection, clamping, resize dimensions |
| `screenshotState.js` (create) | Pure model: tray item descriptors by phase, accelerator validation |
| `picker.html` (create) | Capture-area picker overlay markup + drag logic |
| `picker-preload.js` (create) | Bridges the overlay's pick/cancel to main |
| `hotkey.html` (create) | Hotkey configuration window |
| `hotkey-preload.js` (create) | Bridges the hotkey window to main |
| `main.js` (modify) | Capture target state, `desktopCapturer` grab, IPC, tray items, `globalShortcut` lifecycle |
| `preload.js` (modify) | Exposes `captureScreenshot`, `changeCaptureArea`, `onScreenshotCaptured`, `canCaptureScreenshot` |

**Domain / API**

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/MeetingScreenshot.cs` (create) | Entity |
| `src/Diariz.Domain/DiarizDbContext.cs` (modify) | `DbSet` + model config |
| `src/Diariz.Domain/Migrations/*_AddMeetingScreenshots.cs` (generated) | Migration |
| `src/Diariz.Api/Controllers/ScreenshotsController.cs` (create) | CRUD + content/thumb endpoints |
| `src/Diariz.Api/Contracts/ApiDtos.cs` (modify) | `ScreenshotDto` |
| `src/Diariz.Api/Configuration/AppOptions.cs` (modify) | `ScreenshotOptions` |
| `src/Diariz.Api/Services/StorageUsage.cs` (modify) | Count screenshot bytes toward quota |
| `src/Diariz.Api/Controllers/RecordingsController.cs` (modify) | Blob cleanup on delete; screenshot times in the merge-break union |

**Web (`apps/web/src/`)**

| File | Responsibility |
|---|---|
| `lib/types.ts` (modify) | `Screenshot` interface |
| `lib/api.ts` (modify) | List/create/delete + self-authenticating content and thumb URLs |
| `lib/pendingScreenshots.ts` (create) | Durable IndexedDB stash for captures taken before upload |
| `lib/transcriptNotes.ts` (modify) | Weave notes **and** screenshots |
| `components/Recorder.tsx` (modify) | Receive captures, stamp clock, stash, attach after upload, retry |
| `components/ScreenshotModal.tsx` (create) | Full-image viewer with prev/next, jump, download, delete |
| `components/ScreenshotStrip.tsx` (create) | Shared thumbnail list (live popover + Notes section) |
| `components/ScreenshotsSection.tsx` (create) | Collapsed "Screenshots (n)" block for the Notes tab |
| `components/hub/NotesPopover.tsx` (modify) | Live strip + "Change capture area" |
| `pages/RecordingDetail.tsx` (modify) | Screenshot query, transcript row, Notes tab section, modal wiring |
| `locales/{en,de,es,fr}/workspace.json` (modify) | Strings |

---

## Task 1: Capture geometry (pure)

**Files:**
- Create: `apps/desktop/src/captureTarget.js`
- Test: `apps/desktop/src/captureTarget.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `cropRectFor(display, selection)` - `display` is `{ id: number, bounds: {x,y,width,height}, scaleFactor: number }`, `selection` is `{ kind: "screen", displayId }` or `{ kind: "region", displayId, rect: {x,y,width,height} }` where `rect` is in display-relative DIP. Returns `{x,y,width,height}` in **physical pixels**, or `null` for a whole-screen selection (no crop needed).
  - `clampRect(rect, bounds)` - clamps a DIP rect to `{width,height}` bounds, returns `{x,y,width,height}`.
  - `resizeDims(width, height, maxLongEdge)` - returns `{width, height}` scaled so the long edge is at most `maxLongEdge`, preserving aspect ratio and never upscaling.

- [ ] **Step 1: Write the failing test**

Create `apps/desktop/src/captureTarget.test.js`:

```js
"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { cropRectFor, clampRect, resizeDims } = require("./captureTarget");

const display = { id: 1, bounds: { x: 0, y: 0, width: 1920, height: 1080 }, scaleFactor: 1 };
const hidpi = { id: 2, bounds: { x: 0, y: 0, width: 1920, height: 1080 }, scaleFactor: 2 };

test("a whole-screen selection needs no crop", () => {
  assert.equal(cropRectFor(display, { kind: "screen", displayId: 1 }), null);
});

test("a region selection maps DIP to physical pixels at scale 1", () => {
  const rect = cropRectFor(display, { kind: "region", displayId: 1, rect: { x: 10, y: 20, width: 300, height: 200 } });
  assert.deepEqual(rect, { x: 10, y: 20, width: 300, height: 200 });
});

test("a region selection scales by the display scale factor", () => {
  const rect = cropRectFor(hidpi, { kind: "region", displayId: 2, rect: { x: 10, y: 20, width: 300, height: 200 } });
  assert.deepEqual(rect, { x: 20, y: 40, width: 600, height: 400 });
});

test("a region selection is clamped to the display", () => {
  const rect = cropRectFor(display, { kind: "region", displayId: 1, rect: { x: 1800, y: 1000, width: 400, height: 400 } });
  assert.deepEqual(rect, { x: 1800, y: 1000, width: 120, height: 80 });
});

test("clampRect pulls negative origins back to zero without growing the rect past the bounds", () => {
  assert.deepEqual(clampRect({ x: -50, y: -10, width: 200, height: 100 }, { width: 800, height: 600 }), {
    x: 0, y: 0, width: 150, height: 90,
  });
});

test("resizeDims leaves an already-small image alone", () => {
  assert.deepEqual(resizeDims(800, 600, 2560), { width: 800, height: 600 });
});

test("resizeDims scales a wide image down by its long edge", () => {
  assert.deepEqual(resizeDims(3840, 2160, 2560), { width: 2560, height: 1440 });
});

test("resizeDims scales a tall image down by its long edge", () => {
  assert.deepEqual(resizeDims(1000, 4000, 2000), { width: 500, height: 2000 });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/desktop && node --test src/captureTarget.test.js
```

Expected: FAIL with `Cannot find module './captureTarget'`.

- [ ] **Step 3: Write the minimal implementation**

Create `apps/desktop/src/captureTarget.js`:

```js
"use strict";

// Pure geometry for screen capture. `main.js` owns desktopCapturer and nativeImage;
// the maths lives here so it can be unit-tested without Electron.
//
// Coordinate systems: a picker selection is in display-relative DIP (what the overlay
// window sees); a crop rect is in physical pixels (what a full-resolution grab uses).

/// Clamp a rect to `{width,height}` bounds, pulling the origin in and shrinking the
/// size rather than letting either escape the display.
function clampRect(rect, bounds) {
  const x = Math.max(0, Math.min(Math.round(rect.x), bounds.width));
  const y = Math.max(0, Math.min(Math.round(rect.y), bounds.height));
  const right = Math.min(bounds.width, Math.round(rect.x) + Math.round(rect.width));
  const bottom = Math.min(bounds.height, Math.round(rect.y) + Math.round(rect.height));
  return { x, y, width: Math.max(0, right - x), height: Math.max(0, bottom - y) };
}

/// The physical-pixel crop for a selection, or null when the whole screen is wanted
/// (the grab is already exactly that image, so cropping would be wasted work).
function cropRectFor(display, selection) {
  if (!selection || selection.kind === "screen") return null;
  const scale = display.scaleFactor || 1;
  const clamped = clampRect(selection.rect, display.bounds);
  return {
    x: Math.round(clamped.x * scale),
    y: Math.round(clamped.y * scale),
    width: Math.round(clamped.width * scale),
    height: Math.round(clamped.height * scale),
  };
}

/// Dimensions scaled so the long edge is at most `maxLongEdge`. Never upscales.
function resizeDims(width, height, maxLongEdge) {
  const long = Math.max(width, height);
  if (long <= maxLongEdge) return { width, height };
  const ratio = maxLongEdge / long;
  return { width: Math.round(width * ratio), height: Math.round(height * ratio) };
}

module.exports = { clampRect, cropRectFor, resizeDims };
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/desktop && node --test src/captureTarget.test.js
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/captureTarget.js apps/desktop/src/captureTarget.test.js
git commit -m "feat(desktop): pure capture geometry for screenshots"
```

---

## Task 2: Screenshot tray model and accelerator validation (pure)

**Files:**
- Create: `apps/desktop/src/screenshotState.js`
- Test: `apps/desktop/src/screenshotState.test.js`

**Interfaces:**
- Consumes: the recorder state shape from `recorderState.js` - `{ phase: "idle"|"recording"|"uploading"|"error", source?, ready? }`.
- Produces:
  - `DEFAULT_ACCELERATOR` - the string `"CommandOrControl+Shift+9"`.
  - `trayScreenshotItems(state)` - array of `{ id: "capture"|"change-area", label, enabled }`. Both items are enabled only while `phase === "recording"`; returns `[]` in any other phase so the tray does not show dead entries.
  - `isValidAccelerator(input)` - `true` when the string is a plausible Electron accelerator (at least one modifier plus exactly one key).
  - `normalizeAccelerator(input)` - trims, collapses whitespace, title-cases known modifiers; returns `null` when invalid.

- [ ] **Step 1: Write the failing test**

Create `apps/desktop/src/screenshotState.test.js`:

```js
"use strict";

const test = require("node:test");
const assert = require("node:assert");
const {
  DEFAULT_ACCELERATOR,
  trayScreenshotItems,
  isValidAccelerator,
  normalizeAccelerator,
} = require("./screenshotState");

test("the default accelerator is a valid one", () => {
  assert.equal(isValidAccelerator(DEFAULT_ACCELERATOR), true);
});

test("no screenshot items exist while idle", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "idle", ready: true }), []);
});

test("no screenshot items exist while uploading", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "uploading" }), []);
});

test("capture and change-area items appear while recording", () => {
  const items = trayScreenshotItems({ phase: "recording", source: "mic" });
  assert.deepEqual(items.map((i) => i.id), ["capture", "change-area"]);
  assert.ok(items.every((i) => i.enabled));
  assert.equal(items[0].label, "Capture Screenshot");
  assert.equal(items[1].label, "Change Capture Area...");
});

test("an accelerator with no modifier is rejected", () => {
  assert.equal(isValidAccelerator("S"), false);
});

test("an accelerator with no key is rejected", () => {
  assert.equal(isValidAccelerator("Control+Shift"), false);
});

test("an empty accelerator is rejected", () => {
  assert.equal(isValidAccelerator("   "), false);
});

test("normalizeAccelerator tidies casing and spacing", () => {
  assert.equal(normalizeAccelerator(" control + shift + p "), "Control+Shift+P");
});

test("normalizeAccelerator returns null for an invalid combination", () => {
  assert.equal(normalizeAccelerator("Shift"), null);
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/desktop && node --test src/screenshotState.test.js
```

Expected: FAIL with `Cannot find module './screenshotState'`.

- [ ] **Step 3: Write the minimal implementation**

Create `apps/desktop/src/screenshotState.js`:

```js
"use strict";

// Pure model for the tray's screenshot controls and for hotkey validation.
// `main.js` owns the capture itself; the labels and the accelerator rules live
// here so they can be unit-tested without Electron.

const DEFAULT_ACCELERATOR = "CommandOrControl+Shift+9";

const MODIFIERS = new Map(
  ["Command", "Cmd", "Control", "Ctrl", "CommandOrControl", "CmdOrCtrl", "Alt", "Option", "AltGr", "Shift", "Super", "Meta"]
    .map((m) => [m.toLowerCase(), m]),
);

/// The dynamic screenshot menu items for the current phase, as plain descriptors
/// ({ id, label, enabled }). `main.js` maps each `id` to a click handler. Capture only
/// makes sense mid-recording, so no items are offered in any other phase.
function trayScreenshotItems(state) {
  if (!state || state.phase !== "recording") return [];
  return [
    { id: "capture", label: "Capture Screenshot", enabled: true },
    { id: "change-area", label: "Change Capture Area...", enabled: true },
  ];
}

function parts(input) {
  return String(input ?? "")
    .split("+")
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
}

/// A usable accelerator is at least one modifier plus exactly one non-modifier key.
/// Anything else would either fail to register or steal a bare keystroke globally.
function isValidAccelerator(input) {
  const segs = parts(input);
  if (segs.length < 2) return false;
  const mods = segs.filter((s) => MODIFIERS.has(s.toLowerCase()));
  const keys = segs.filter((s) => !MODIFIERS.has(s.toLowerCase()));
  return mods.length >= 1 && keys.length === 1;
}

/// Canonical form of an accelerator, or null when it isn't usable.
function normalizeAccelerator(input) {
  if (!isValidAccelerator(input)) return null;
  return parts(input)
    .map((s) => MODIFIERS.get(s.toLowerCase()) ?? (s.length === 1 ? s.toUpperCase() : s[0].toUpperCase() + s.slice(1)))
    .join("+");
}

module.exports = { DEFAULT_ACCELERATOR, trayScreenshotItems, isValidAccelerator, normalizeAccelerator };
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/desktop && node --test src/screenshotState.test.js
```

Expected: PASS, 9 tests.

- [ ] **Step 5: Run the whole desktop suite and commit**

```bash
cd apps/desktop && npm test
git add apps/desktop/src/screenshotState.js apps/desktop/src/screenshotState.test.js
git commit -m "feat(desktop): pure tray model and accelerator rules for screenshots"
```

Expected: all existing tests still pass.

---

## Task 3: Picker overlay window

**Files:**
- Create: `apps/desktop/src/picker.html`
- Create: `apps/desktop/src/picker-preload.js`

**Interfaces:**
- Consumes: `captureTarget.js` coordinate convention (display-relative DIP).
- Produces: the overlay sends exactly one of
  - `window.picker.choose({ kind: "screen" })`
  - `window.picker.choose({ kind: "region", rect: { x, y, width, height } })` (display-relative DIP)
  - `window.picker.cancel()`

  Main receives these on the IPC channels `picker:choose` and `picker:cancel`, with the sending window identifying which display was used (Task 4 wires this).

There is no automated test for this task - it is markup and drag handling inside a browser window. It is verified manually in Step 3.

- [ ] **Step 1: Create the preload bridge**

Create `apps/desktop/src/picker-preload.js`:

```js
"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// The capture-area picker overlay's only job: report one selection, or a cancel.
// Main matches the sender to the display the overlay covers.
contextBridge.exposeInMainWorld("picker", {
  choose: (selection) => ipcRenderer.send("picker:choose", selection),
  cancel: () => ipcRenderer.send("picker:cancel"),
});
```

- [ ] **Step 2: Create the overlay page**

Create `apps/desktop/src/picker.html`:

```html
<!doctype html>
<meta charset="utf-8" />
<title>Choose capture area</title>
<style>
  html, body { margin: 0; height: 100%; overflow: hidden; cursor: crosshair; user-select: none; }
  body { background: rgba(0, 0, 0, 0.35); font: 14px system-ui, sans-serif; color: #fff; }
  #hint {
    position: fixed; top: 24px; left: 50%; transform: translateX(-50%);
    background: rgba(0, 0, 0, 0.75); padding: 10px 16px; border-radius: 8px; white-space: nowrap;
  }
  #sel {
    position: fixed; display: none; border: 2px solid #4da3ff;
    background: rgba(77, 163, 255, 0.15); pointer-events: none;
  }
</style>
<div id="hint">Drag to capture an area, click for the whole screen, Esc to cancel</div>
<div id="sel"></div>
<script>
  const sel = document.getElementById("sel");
  let start = null;

  // Below this many pixels a drag is treated as a click (whole screen) rather than a
  // tiny unusable rectangle.
  const CLICK_THRESHOLD = 6;

  function rectFrom(a, b) {
    return {
      x: Math.min(a.x, b.x),
      y: Math.min(a.y, b.y),
      width: Math.abs(a.x - b.x),
      height: Math.abs(a.y - b.y),
    };
  }

  addEventListener("mousedown", (e) => {
    start = { x: e.clientX, y: e.clientY };
    sel.style.display = "block";
  });

  addEventListener("mousemove", (e) => {
    if (!start) return;
    const r = rectFrom(start, { x: e.clientX, y: e.clientY });
    sel.style.left = r.x + "px";
    sel.style.top = r.y + "px";
    sel.style.width = r.width + "px";
    sel.style.height = r.height + "px";
  });

  addEventListener("mouseup", (e) => {
    if (!start) return;
    const r = rectFrom(start, { x: e.clientX, y: e.clientY });
    start = null;
    sel.style.display = "none";
    if (r.width < CLICK_THRESHOLD || r.height < CLICK_THRESHOLD) window.picker.choose({ kind: "screen" });
    else window.picker.choose({ kind: "region", rect: r });
  });

  addEventListener("keydown", (e) => {
    if (e.key === "Escape") window.picker.cancel();
  });
</script>
```

- [ ] **Step 3: Commit (manual verification happens in Task 4, once main can open it)**

```bash
git add apps/desktop/src/picker.html apps/desktop/src/picker-preload.js
git commit -m "feat(desktop): capture-area picker overlay"
```

---

## Task 4: Wire capture into the main process

**Files:**
- Modify: `apps/desktop/src/main.js`
- Modify: `apps/desktop/src/preload.js`

**Interfaces:**
- Consumes: `cropRectFor`, `resizeDims` (Task 1); `trayScreenshotItems` (Task 2); the picker IPC channels (Task 3).
- Produces (renderer-visible, via `preload.js`):
  - `window.diariz.canCaptureScreenshot` - `true`.
  - `window.diariz.captureScreenshot()` - `Promise<void>`, asks main to capture now.
  - `window.diariz.changeCaptureArea()` - `Promise<void>`, clears the target and re-opens the picker.
  - `window.diariz.onScreenshotCaptured(cb)` - `cb({ full: ArrayBuffer, thumb: ArrayBuffer, width: number, height: number })`; returns an unsubscribe function.

- [ ] **Step 1: Extend the preload surface**

In `apps/desktop/src/preload.js`, add these entries inside the `contextBridge.exposeInMainWorld("diariz", { ... })` object, after `reportRecorderState`:

```js
  /// True when this shell can capture screenshots (used to show the capture affordances).
  canCaptureScreenshot: true,

  /// Ask main to capture now. The first capture of a recording opens the area picker.
  captureScreenshot: () => ipcRenderer.invoke("screenshot:capture"),

  /// Forget this recording's capture area and re-open the picker.
  changeCaptureArea: () => ipcRenderer.invoke("screenshot:change-area"),

  /// Subscribe to captured images. `cb` receives { full, thumb, width, height } where
  /// `full` and `thumb` are ArrayBuffers (PNG and JPEG). Returns an unsubscribe function.
  onScreenshotCaptured: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("screenshot:captured", listener);
    return () => ipcRenderer.removeListener("screenshot:captured", listener);
  },
```

- [ ] **Step 2: Add the capture module to main.js**

In `apps/desktop/src/main.js`, extend the Electron import on line 5 to include `screen` and `globalShortcut`:

```js
const { app, BrowserWindow, Tray, Menu, Notification, desktopCapturer, ipcMain, shell, nativeImage, screen, globalShortcut } = require("electron");
```

Add the module requires next to the existing `recorderState` require:

```js
const { cropRectFor, resizeDims } = require("./captureTarget");
const { trayScreenshotItems, DEFAULT_ACCELERATOR, normalizeAccelerator } = require("./screenshotState");
```

Add this section immediately above the `// ---- Tray ----` comment (around line 410):

```js
// ---- Screenshot capture ----

const MAX_LONG_EDGE = 2560;
const THUMB_LONG_EDGE = 320;

// The capture area chosen for the CURRENT recording: { displayId, selection } or null.
// Cleared on every transition into "recording" so each meeting picks fresh (a stale
// rectangle from a previous monitor layout would silently capture the wrong thing).
let captureTarget = null;
// Open picker windows, keyed by display id, plus the promise waiting on a choice.
let pickerWindows = new Map();
let pickerResolve = null;

function closePickers() {
  for (const win of pickerWindows.values()) if (!win.isDestroyed()) win.destroy();
  pickerWindows = new Map();
}

/// Show a full-screen overlay on every display and resolve with the chosen target
/// ({ displayId, selection }) or null if the user cancelled.
function openPicker() {
  closePickers();
  return new Promise((resolve) => {
    pickerResolve = resolve;
    for (const display of screen.getAllDisplays()) {
      const win = new BrowserWindow({
        x: display.bounds.x,
        y: display.bounds.y,
        width: display.bounds.width,
        height: display.bounds.height,
        frame: false,
        transparent: true,
        alwaysOnTop: true,
        skipTaskbar: true,
        resizable: false,
        movable: false,
        fullscreenable: false,
        webPreferences: {
          preload: path.join(__dirname, "picker-preload.js"),
          contextIsolation: true,
          sandbox: true,
          nodeIntegration: false,
        },
      });
      win.setAlwaysOnTop(true, "screen-saver");
      win.loadFile(path.join(__dirname, "picker.html"));
      pickerWindows.set(display.id, win);
    }
  });
}

function settlePicker(value) {
  const resolve = pickerResolve;
  pickerResolve = null;
  closePickers();
  if (resolve) resolve(value);
}

ipcMain.on("picker:choose", (event, selection) => {
  let displayId = null;
  for (const [id, win] of pickerWindows) if (win.webContents === event.sender) displayId = id;
  if (displayId === null) return settlePicker(null);
  settlePicker({ displayId, selection });
});

ipcMain.on("picker:cancel", () => settlePicker(null));

/// Grab the target display at full resolution, crop to the chosen area, and return
/// { full, thumb, width, height } - or null if the display has gone away.
async function grab(target) {
  const display = screen.getAllDisplays().find((d) => d.id === target.displayId);
  if (!display) return null; // monitor unplugged since the area was chosen

  const scale = display.scaleFactor || 1;
  const sources = await desktopCapturer.getSources({
    types: ["screen"],
    thumbnailSize: {
      width: Math.round(display.bounds.width * scale),
      height: Math.round(display.bounds.height * scale),
    },
  });
  const source = sources.find((s) => String(s.display_id) === String(display.id)) ?? sources[0];
  if (!source) return null;

  let image = source.thumbnail;
  const crop = cropRectFor(display, target.selection);
  if (crop && crop.width > 0 && crop.height > 0) image = image.crop(crop);

  const size = image.getSize();
  const capped = resizeDims(size.width, size.height, MAX_LONG_EDGE);
  const fullImage = capped.width === size.width ? image : image.resize(capped);
  const thumbDims = resizeDims(capped.width, capped.height, THUMB_LONG_EDGE);
  const thumbImage = fullImage.resize(thumbDims);

  return {
    full: fullImage.toPNG(),
    thumb: thumbImage.toJPEG(80),
    width: capped.width,
    height: capped.height,
  };
}

/// Capture now: pick an area first if this recording hasn't chosen one, then grab and
/// push the bytes to the renderer (which owns the recording clock).
async function captureScreenshot() {
  if (recorder.phase !== "recording" || !mainWindow) return;
  if (!captureTarget) {
    captureTarget = await openPicker();
    if (!captureTarget) return; // cancelled - no capture, no error
  }
  const shot = await grab(captureTarget);
  if (!shot) {
    captureTarget = null; // display gone: re-prompt on the next capture
    return;
  }
  mainWindow.webContents.send("screenshot:captured", shot);
}

async function changeCaptureArea() {
  captureTarget = null;
  if (recorder.phase !== "recording") return;
  captureTarget = await openPicker();
}

ipcMain.handle("screenshot:capture", () => captureScreenshot());
ipcMain.handle("screenshot:change-area", () => changeCaptureArea());

/// The hotkey is registered only while recording, so Diariz never holds a global key
/// while idle. Returns false when the combination is already taken by other software.
function applyShortcut() {
  globalShortcut.unregisterAll();
  if (recorder.phase !== "recording") return true;
  const accelerator = normalizeAccelerator(store.get("captureHotkey")) ?? DEFAULT_ACCELERATOR;
  try {
    return globalShortcut.register(accelerator, () => void captureScreenshot());
  } catch {
    return false;
  }
}
```

- [ ] **Step 3: Reset the target and shortcut on phase changes**

In `applyRecorderState` (around line 199), immediately after the `recordingStartedAt` assignment, add the target reset:

```js
  if (next.phase === "recording" && prev.phase !== "recording") {
    recordingStartedAt = Date.now();
    captureTarget = null; // each recording chooses its own capture area
  }
```

At the end of `applyRecorderState`, immediately before `refreshTray();`, add:

```js
  applyShortcut();
```

Add a teardown so the shortcut never outlives the app - next to the existing quit handling:

```js
app.on("will-quit", () => globalShortcut.unregisterAll());
```

- [ ] **Step 4: Add the tray items**

In `refreshTray()`, after the `recordItems` block, add:

```js
  const shotItems = trayScreenshotItems(recorder).map((item) => ({
    label: item.label,
    enabled: item.enabled,
    click: () => {
      if (item.id === "capture") void captureScreenshot();
      else if (item.id === "change-area") void changeCaptureArea();
    },
  }));
```

and insert `...shotItems,` into the `Menu.buildFromTemplate([...])` array immediately after `...recordItems,`.

- [ ] **Step 5: Verify manually**

```bash
cd apps/desktop && npm run dev
```

Expected, against a running dev server: starting a recording enables "Capture Screenshot" in the tray; the first capture opens the dimmed overlay on every monitor; dragging a rectangle closes it; the DevTools console of the main window shows a `screenshot:captured` message arriving (nothing consumes it yet - Task 13 does). Escape cancels with no error. Stopping the recording removes the tray items.

- [ ] **Step 6: Run the desktop suite and commit**

```bash
cd apps/desktop && npm test
git add apps/desktop/src/main.js apps/desktop/src/preload.js
git commit -m "feat(desktop): capture screenshots from the tray and a global hotkey"
```

---

## Task 5: Hotkey configuration window

**Files:**
- Create: `apps/desktop/src/hotkey.html`
- Create: `apps/desktop/src/hotkey-preload.js`
- Modify: `apps/desktop/src/main.js`

**Interfaces:**
- Consumes: `normalizeAccelerator`, `DEFAULT_ACCELERATOR` (Task 2); `applyShortcut` (Task 4).
- Produces: persisted `electron-store` key `captureHotkey` (a normalized accelerator string). `window.hotkeyConfig.load()` returns `Promise<string>`; `window.hotkeyConfig.save(accelerator)` returns `Promise<{ ok: boolean, error?: string }>`.

- [ ] **Step 1: Create the preload bridge**

Create `apps/desktop/src/hotkey-preload.js`:

```js
"use strict";

const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("hotkeyConfig", {
  load: () => ipcRenderer.invoke("hotkey:load"),
  save: (accelerator) => ipcRenderer.invoke("hotkey:save", accelerator),
});
```

- [ ] **Step 2: Create the window**

Create `apps/desktop/src/hotkey.html`:

```html
<!doctype html>
<meta charset="utf-8" />
<title>Screenshot hotkey</title>
<style>
  body { font: 14px system-ui, sans-serif; margin: 20px; }
  input { width: 100%; box-sizing: border-box; padding: 8px; font: inherit; margin: 10px 0; }
  #error { color: #b00020; min-height: 18px; }
  button { padding: 6px 14px; font: inherit; }
</style>
<h3 style="margin-top:0">Screenshot hotkey</h3>
<p>Press the keys you want to use while a recording is running.</p>
<input id="combo" readonly placeholder="Click here, then press your combination" />
<div id="error"></div>
<button id="save">Save</button>
<script>
  const input = document.getElementById("combo");
  const error = document.getElementById("error");
  let pending = null;

  window.hotkeyConfig.load().then((current) => {
    input.value = current;
    pending = current;
  });

  input.addEventListener("keydown", (e) => {
    e.preventDefault();
    const mods = [];
    if (e.ctrlKey) mods.push("Control");
    if (e.metaKey) mods.push("Command");
    if (e.altKey) mods.push("Alt");
    if (e.shiftKey) mods.push("Shift");
    const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
    if (["Control", "Shift", "Alt", "Meta"].includes(key)) return; // modifier alone
    pending = [...mods, key].join("+");
    input.value = pending;
  });

  document.getElementById("save").addEventListener("click", async () => {
    error.textContent = "";
    const res = await window.hotkeyConfig.save(pending);
    if (res.ok) window.close();
    else error.textContent = res.error;
  });
</script>
```

- [ ] **Step 3: Handle it in main.js**

Add to `apps/desktop/src/main.js`, below the screenshot section:

```js
let hotkeyWindow = null;

function showHotkeyWindow() {
  if (hotkeyWindow) {
    hotkeyWindow.focus();
    return;
  }
  hotkeyWindow = new BrowserWindow({
    width: 420,
    height: 260,
    resizable: false,
    title: "Diariz - Screenshot hotkey",
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "hotkey-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  hotkeyWindow.setMenuBarVisibility(false);
  hotkeyWindow.loadFile(path.join(__dirname, "hotkey.html"));
  hotkeyWindow.on("closed", () => {
    hotkeyWindow = null;
  });
}

ipcMain.handle("hotkey:load", () => normalizeAccelerator(store.get("captureHotkey")) ?? DEFAULT_ACCELERATOR);

// Save only if the combination is both well-formed AND actually registrable - otherwise
// the user would set a hotkey that silently never fires because another app owns it.
ipcMain.handle("hotkey:save", (_event, accelerator) => {
  const normalized = normalizeAccelerator(accelerator);
  if (!normalized) return { ok: false, error: "Use at least one modifier (Ctrl, Alt, Shift) plus one key." };
  const previous = store.get("captureHotkey");
  store.set("captureHotkey", normalized);
  if (recorder.phase === "recording" && !applyShortcut()) {
    if (previous) store.set("captureHotkey", previous);
    else store.delete("captureHotkey");
    applyShortcut();
    return { ok: false, error: "That combination is already in use by another application." };
  }
  return { ok: true };
});
```

Add a tray entry in `refreshTray()`, immediately before the existing `{ label: "Settings…", ... }` line:

```js
      { label: "Screenshot Hotkey...", click: () => showHotkeyWindow() },
```

- [ ] **Step 4: Verify manually**

```bash
cd apps/desktop && npm run dev
```

Expected: the tray shows "Screenshot Hotkey..."; the window opens, shows `CommandOrControl+Shift+9`, accepts a new combination, and rejects a modifier-free one with the validation message. While recording, the saved hotkey captures a screenshot with the Diariz window unfocused.

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/hotkey.html apps/desktop/src/hotkey-preload.js apps/desktop/src/main.js
git commit -m "feat(desktop): configurable screenshot hotkey"
```

---

## Task 6: MeetingScreenshot entity and migration

**Files:**
- Create: `src/Diariz.Domain/Entities/MeetingScreenshot.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.Tests/MeetingScreenshotEntityTests.cs`

**Interfaces:**
- Produces: `MeetingScreenshot` with properties `Id`, `UserId`, `User`, `RecordingId`, `Recording`, `CapturedAtMs` (long), `BlobKey` (string), `ThumbBlobKey` (string), `Width` (int), `Height` (int), `SizeBytes` (long), `Ordinal` (int), `CreatedAt` (DateTimeOffset); `DiarizDbContext.MeetingScreenshots`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/MeetingScreenshotEntityTests.cs`:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingScreenshotEntityTests
{
    [Fact]
    public async Task Screenshot_RoundTrips_WithItsCaptureFacts()
    {
        using var db = TestDb.Create();
        var id = Guid.NewGuid();
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = id,
            UserId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            CapturedAtMs = 42_000,
            BlobKey = "user/screenshots/a.png",
            ThumbBlobKey = "user/screenshots/a.thumb.jpg",
            Width = 1920,
            Height = 1080,
            SizeBytes = 1234,
            Ordinal = 0,
        });
        await db.SaveChangesAsync();

        var stored = await db.MeetingScreenshots.SingleAsync(s => s.Id == id);
        Assert.Equal(42_000, stored.CapturedAtMs);
        Assert.Equal("user/screenshots/a.thumb.jpg", stored.ThumbBlobKey);
        Assert.Equal(1920, stored.Width);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingScreenshotEntity"
```

Expected: FAIL - `MeetingScreenshot` does not exist (compile error).

- [ ] **Step 3: Create the entity**

Create `src/Diariz.Domain/Entities/MeetingScreenshot.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A screen capture taken during a recording from the desktop client. <see cref="CapturedAtMs"/>
/// is the offset into the *recorded* clock (pause-aware, stamped by the recorder) - an immutable capture
/// fact, which is why it is non-nullable here even though <see cref="MeetingNote.CapturedAtMs"/> is not.
/// Two blobs are stored per capture: the full PNG and a small JPEG thumbnail for inline rendering.</summary>
public class MeetingScreenshot
{
    public Guid Id { get; set; }

    /// <summary>Owner - the recording's owner at capture time.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Offset (ms) into the recording clock. Not user-editable.</summary>
    public long CapturedAtMs { get; set; }

    /// <summary>Object-storage key for the full PNG.</summary>
    public string BlobKey { get; set; } = string.Empty;
    /// <summary>Object-storage key for the JPEG thumbnail.</summary>
    public string ThumbBlobKey { get; set; } = string.Empty;

    /// <summary>Pixel dimensions of the full image.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Full plus thumbnail bytes; counts toward the owner's storage quota.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Sort order within the recording (0-based).</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 4: Register it on the context**

In `src/Diariz.Domain/DiarizDbContext.cs`, add the set next to `MeetingNotes` (line 28):

```csharp
    public DbSet<MeetingScreenshot> MeetingScreenshots => Set<MeetingScreenshot>();
```

and add the model config immediately after the `MeetingNote` block (around line 264). Plain columns only, so it stays outside the Npgsql guard and loads under the in-memory provider:

```csharp
        builder.Entity<MeetingScreenshot>(e =>
        {
            e.HasIndex(s => new { s.RecordingId, s.CapturedAtMs });
            e.Property(s => s.BlobKey).HasMaxLength(512);
            e.Property(s => s.ThumbBlobKey).HasMaxLength(512);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Recording).WithMany().HasForeignKey(s => s.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingScreenshotEntity"
```

Expected: PASS.

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add AddMeetingScreenshots --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Expected: a new pair of files under `src/Diariz.Domain/Migrations/`. Open the generated `.cs` and confirm it creates one `MeetingScreenshots` table with cascade FKs to `AspNetUsers` and `Recordings` and an index on `(RecordingId, CapturedAtMs)`. The change is purely additive, so **do not** bump `MaintenanceController.CurrentFormat`.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain tests/Diariz.Api.Tests/MeetingScreenshotEntityTests.cs
git commit -m "feat(api): MeetingScreenshot entity and migration"
```

---

## Task 7: Screenshots API

**Files:**
- Create: `src/Diariz.Api/Controllers/ScreenshotsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs`
- Test: `tests/Diariz.Api.Tests/ScreenshotsControllerTests.cs`

**Interfaces:**
- Consumes: `MeetingScreenshot` (Task 6); `IAudioStorage.UploadAsync(key, stream, contentType)`, `IAudioStorage.OpenReadAsync(key)`, `IAudioStorage.DeleteAsync(key)`; `IStorageUsage.UsedBytesAsync(userId)`; `Http.Context(userId)` and `FakeAudioStorage` from `Diariz.Api.TestSupport`.
- Produces:
  - `ScreenshotDto(Guid Id, long CapturedAtMs, int Width, int Height, long SizeBytes, int Ordinal, DateTimeOffset CreatedAt)`.
  - `ScreenshotOptions { long MaxBytes }`, bound from the `Screenshots` config section, default 20 MB.
  - Routes under `api/recordings/{recordingId:guid}/screenshots`: `POST /`, `GET /`, `GET /{screenshotId}/content`, `GET /{screenshotId}/thumb`, `DELETE /{screenshotId}`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/ScreenshotsControllerTests.cs`:

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ScreenshotsControllerTests
{
    private static IFormFile Png(int bytes = 64) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "full", "shot.png") { Headers = new HeaderDictionary() };

    private static IFormFile Jpg(int bytes = 16) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "thumb", "shot.jpg") { Headers = new HeaderDictionary() };

    private static (ScreenshotsController Controller, DiarizDbContext Db, FakeAudioStorage Storage, Guid UserId, Guid RecordingId)
        Setup(long quotaBytes = 1024 * 1024)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "a@b.c", UserName = "a@b.c", QuotaBytes = quotaBytes });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.SaveChanges();

        var storage = new FakeAudioStorage();
        var controller = new ScreenshotsController(
            db, storage, new StorageUsage(db), Options.Create(new ScreenshotOptions()))
        {
            ControllerContext = Http.Context(userId),
        };
        return (controller, db, storage, userId, recordingId);
    }

    [Fact]
    public async Task Create_StoresBothBlobsAndTheCaptureFacts()
    {
        var (controller, db, storage, userId, recordingId) = Setup();

        var result = await controller.Create(recordingId, Png(), Jpg(), capturedAtMs: 12_500, width: 1920, height: 1080);

        var dto = Assert.IsType<ScreenshotDto>(result.Value);
        Assert.Equal(12_500, dto.CapturedAtMs);
        Assert.Equal(80, dto.SizeBytes); // 64 + 16

        var row = await db.MeetingScreenshots.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Equal($"{userId}/screenshots/{row.Id}.png", row.BlobKey);
        Assert.Equal($"{userId}/screenshots/{row.Id}.thumb.jpg", row.ThumbBlobKey);
        Assert.Contains(row.BlobKey, storage.Objects.Keys);
        Assert.Contains(row.ThumbBlobKey, storage.Objects.Keys);
    }

    [Fact]
    public async Task Create_AssignsIncreasingOrdinals()
    {
        var (controller, _, _, _, recordingId) = Setup();

        await controller.Create(recordingId, Png(), Jpg(), 1_000, 100, 100);
        var second = await controller.Create(recordingId, Png(), Jpg(), 2_000, 100, 100);

        Assert.Equal(1, Assert.IsType<ScreenshotDto>(second.Value).Ordinal);
    }

    [Fact]
    public async Task Create_ForAnotherUsersRecording_ReturnsNotFound()
    {
        var (controller, db, _, _, _) = Setup();
        var otherId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = otherId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        var result = await controller.Create(otherId, Png(), Jpg(), 0, 10, 10);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_OverQuota_IsRejectedAndStoresNothing()
    {
        var (controller, db, storage, _, recordingId) = Setup(quotaBytes: 50);

        var result = await controller.Create(recordingId, Png(64), Jpg(16), 0, 10, 10);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
        Assert.Empty(storage.Objects);
        Assert.Empty(db.MeetingScreenshots);
    }

    [Fact]
    public async Task Create_OverTheSizeCap_IsRejected()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = "a@b.c", UserName = "a@b.c", QuotaBytes = long.MaxValue });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.SaveChanges();
        var controller = new ScreenshotsController(
            db, new FakeAudioStorage(), new StorageUsage(db), Options.Create(new ScreenshotOptions { MaxBytes = 32 }))
        {
            ControllerContext = Http.Context(userId),
        };

        var result = await controller.Create(recordingId, Png(64), Jpg(16), 0, 10, 10);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsTheRecordingsCapturesInCaptureOrder()
    {
        var (controller, _, _, _, recordingId) = Setup();
        await controller.Create(recordingId, Png(), Jpg(), 9_000, 10, 10);
        await controller.Create(recordingId, Png(), Jpg(), 3_000, 10, 10);

        var list = await controller.List(recordingId);

        Assert.Equal(new long[] { 3_000, 9_000 }, list.Value!.Select(s => s.CapturedAtMs).ToArray());
    }

    [Fact]
    public async Task Delete_RemovesTheRowAndBothBlobs()
    {
        var (controller, db, storage, _, recordingId) = Setup();
        var created = Assert.IsType<ScreenshotDto>((await controller.Create(recordingId, Png(), Jpg(), 0, 10, 10)).Value);

        var result = await controller.Delete(recordingId, created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.MeetingScreenshots);
        Assert.Empty(storage.Objects);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ScreenshotsController"
```

Expected: FAIL - `ScreenshotsController` and `ScreenshotOptions` do not exist (compile error).

- [ ] **Step 3: Add the DTO and options**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, next to `MeetingNoteDto`:

```csharp
/// <summary>A screen capture taken during a recording. Bytes are fetched separately from the content and
/// thumb endpoints; this carries only what the list and the transcript row need.</summary>
public record ScreenshotDto(
    Guid Id, long CapturedAtMs, int Width, int Height, long SizeBytes, int Ordinal, DateTimeOffset CreatedAt);
```

In `src/Diariz.Api/Configuration/AppOptions.cs`, next to `AttachmentOptions`:

```csharp
/// <summary>Limits for meeting screenshots (bound from the "Screenshots" section).</summary>
public class ScreenshotOptions
{
    /// <summary>Maximum combined bytes (full image plus thumbnail) for one capture.</summary>
    public long MaxBytes { get; set; } = 20L * 1024 * 1024;
}
```

Register it in `src/Diariz.Api/Program.cs`, next to the other `Configure<...>` calls:

```csharp
builder.Services.Configure<ScreenshotOptions>(builder.Configuration.GetSection("Screenshots"));
```

- [ ] **Step 4: Write the controller**

Create `src/Diariz.Api/Controllers/ScreenshotsController.cs`:

```csharp
using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Screen captures taken during a recording from the desktop client. Each capture stores two
/// blobs (full PNG plus JPEG thumbnail) and counts toward the owner's storage quota. The content and thumb
/// endpoints accept the bearer via <c>access_token</c> (see Program.cs) so an &lt;img&gt; tag can load them
/// directly - an image request cannot carry an Authorization header.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/screenshots")]
public class ScreenshotsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IStorageUsage _usage;
    private readonly ScreenshotOptions _options;

    public ScreenshotsController(
        DiarizDbContext db, IAudioStorage storage, IStorageUsage usage, IOptions<ScreenshotOptions> options)
    {
        _db = db;
        _storage = storage;
        _usage = usage;
        _options = options.Value;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static ScreenshotDto ToDto(MeetingScreenshot s) =>
        new(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScreenshotDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        return await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .OrderBy(s => s.CapturedAtMs)
            .Select(s => new ScreenshotDto(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Store one capture. The recorder uploads these after the recording row exists, so a capture
    /// taken mid-meeting arrives here only once its audio has landed.</summary>
    [HttpPost]
    [RequestSizeLimit(50L * 1024 * 1024)]
    public async Task<ActionResult<ScreenshotDto>> Create(
        Guid recordingId,
        [FromForm] IFormFile? full,
        [FromForm] IFormFile? thumb,
        [FromForm] long capturedAtMs,
        [FromForm] int width,
        [FromForm] int height)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        if (full is null || full.Length == 0 || thumb is null || thumb.Length == 0)
            return BadRequest("Both the full image and its thumbnail are required.");
        if (capturedAtMs < 0) return BadRequest("capturedAtMs must not be negative.");

        var total = full.Length + thumb.Length;
        if (total > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Screenshot too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used + total > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings, attachments or screenshots, or ask an administrator to raise your quota.");

        var id = Guid.NewGuid();
        var blobKey = $"{UserId}/screenshots/{id}.png";
        var thumbKey = $"{UserId}/screenshots/{id}.thumb.jpg";

        await using (var stream = full.OpenReadStream())
            await _storage.UploadAsync(blobKey, stream, "image/png");
        await using (var stream = thumb.OpenReadStream())
            await _storage.UploadAsync(thumbKey, stream, "image/jpeg");

        var next = (await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .Select(s => (int?)s.Ordinal)
            .MaxAsync() ?? -1) + 1;

        var shot = new MeetingScreenshot
        {
            Id = id,
            UserId = UserId,
            RecordingId = recordingId,
            CapturedAtMs = capturedAtMs,
            BlobKey = blobKey,
            ThumbBlobKey = thumbKey,
            Width = width,
            Height = height,
            SizeBytes = total,
            Ordinal = next,
        };
        _db.MeetingScreenshots.Add(shot);
        await _db.SaveChangesAsync();
        return ToDto(shot);
    }

    [HttpGet("{screenshotId:guid}/content")]
    public Task<IActionResult> Content(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: false);

    [HttpGet("{screenshotId:guid}/thumb")]
    public Task<IActionResult> Thumb(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: true);

    private async Task<IActionResult> StreamAsync(Guid recordingId, Guid screenshotId, bool thumbnail)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var shot = await _db.MeetingScreenshots
            .FirstOrDefaultAsync(s => s.Id == screenshotId && s.RecordingId == recordingId);
        if (shot is null) return NotFound();

        var stream = await _storage.OpenReadAsync(thumbnail ? shot.ThumbBlobKey : shot.BlobKey);
        return File(stream, thumbnail ? "image/jpeg" : "image/png");
    }

    /// <summary>Remove a capture. Blobs go first: a dangling row is safer (and retriable) than an orphaned blob.</summary>
    [HttpDelete("{screenshotId:guid}")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid screenshotId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var shot = await _db.MeetingScreenshots
            .FirstOrDefaultAsync(s => s.Id == screenshotId && s.RecordingId == recordingId);
        if (shot is null) return NotFound();

        await _storage.DeleteAsync(shot.BlobKey);
        await _storage.DeleteAsync(shot.ThumbBlobKey);
        _db.MeetingScreenshots.Remove(shot);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ScreenshotsController"
```

Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests/ScreenshotsControllerTests.cs
git commit -m "feat(api): screenshots endpoints with quota and size limits"
```

---

## Task 8: Quota accounting and recording-delete cleanup

**Files:**
- Modify: `src/Diariz.Api/Services/StorageUsage.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (the delete path around lines 994-1010)
- Test: `tests/Diariz.Api.Tests/StorageUsageTests.cs` (create if absent, otherwise extend)

**Interfaces:**
- Consumes: `MeetingScreenshot` (Task 6).
- Produces: `IStorageUsage.UsedBytesAsync` includes screenshot bytes; recording delete removes both screenshot blobs.

- [ ] **Step 1: Write the failing tests**

Create (or extend) `tests/Diariz.Api.Tests/StorageUsageTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class StorageUsageTests
{
    [Fact]
    public async Task UsedBytes_IncludesScreenshotBytes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", SizeBytes = 1_000 });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecordingId = recordingId,
            CapturedAtMs = 0,
            BlobKey = "k.png",
            ThumbBlobKey = "k.thumb.jpg",
            SizeBytes = 250,
        });
        await db.SaveChangesAsync();

        var used = await new StorageUsage(db).UsedBytesAsync(userId);

        Assert.Equal(1_250, used);
    }

    [Fact]
    public async Task UsedBytes_IgnoresAnotherUsersScreenshots()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            CapturedAtMs = 0,
            BlobKey = "k.png",
            ThumbBlobKey = "k.thumb.jpg",
            SizeBytes = 900,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await new StorageUsage(db).UsedBytesAsync(userId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~StorageUsage"
```

Expected: FAIL - `UsedBytes_IncludesScreenshotBytes` reports 1000, not 1250.

- [ ] **Step 3: Count screenshots toward the quota**

In `src/Diariz.Api/Services/StorageUsage.cs`, add before the `return`:

```csharp
        var screenshots = await db.MeetingScreenshots
            .Where(s => s.UserId == userId)
            .SumAsync(s => s.SizeBytes, ct);
```

and change the return to:

```csharp
        return audio + attachments + sectionAttachments + screenshots;
```

Update the interface doc comment to mention screenshots:

```csharp
/// <summary>Computes a user's used storage as the total bytes of their recorded audio plus any uploaded
/// attachment files and meeting screenshots (DB rows/derived data don't count toward the quota).</summary>
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~StorageUsage"
```

Expected: PASS, 2 tests.

- [ ] **Step 5: Clean up screenshot blobs on recording delete**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, in the delete path, after the existing attachment-key loop:

```csharp
        foreach (var key in await FileAttachmentKeysAsync(rec.Id))
            await _storage.DeleteAsync(key);
        foreach (var key in await ScreenshotKeysAsync(rec.Id))
            await _storage.DeleteAsync(key);
```

and add the helper next to `FileAttachmentKeysAsync`:

```csharp
    /// <summary>Object-storage keys of a recording's screenshots - full image and thumbnail alike.</summary>
    private async Task<List<string>> ScreenshotKeysAsync(Guid recordingId) =>
        await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .SelectMany(s => new[] { s.BlobKey, s.ThumbBlobKey })
            .ToListAsync();
```

Update the surrounding comment so it stays accurate:

```csharp
        // The DB cascade clears Transcriptions -> Segments + Summary, Speakers, Attachment and
        // MeetingScreenshot rows - but not their object-storage blobs, so the uploaded-attachment files
        // and the screenshot images must be deleted explicitly too.
```

- [ ] **Step 6: Write and run a delete-cleanup test**

Add to `tests/Diariz.Api.Tests/RecordingsControllerTests.cs` (follow that file's existing setup helpers for constructing the controller):

```csharp
    [Fact]
    public async Task Delete_RemovesScreenshotBlobsToo()
    {
        var (controller, db, storage, userId) = SetupRecordingsController();
        var recordingId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "a.webm" });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecordingId = recordingId,
            CapturedAtMs = 0,
            BlobKey = "shot.png",
            ThumbBlobKey = "shot.thumb.jpg",
            SizeBytes = 10,
        });
        await db.SaveChangesAsync();
        storage.Objects["a.webm"] = [];
        storage.Objects["shot.png"] = [];
        storage.Objects["shot.thumb.jpg"] = [];

        await controller.Delete(recordingId);

        Assert.Empty(storage.Objects);
    }
```

If `SetupRecordingsController` does not already exist in that file, use whatever construction the neighbouring tests use and keep the assertions identical.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingsController"
```

Expected: PASS, including the new test.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests
git commit -m "feat(api): count screenshots toward quota and clean their blobs on delete"
```

---

## Task 9: Screenshots break same-speaker merges

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (around lines 466-478)
- Test: `tests/Diariz.Api.IntegrationTests/ScreenshotMergeBreakTests.cs`

**Interfaces:**
- Consumes: `TranscriptNoteAnchor.BreakBeforeIndices(IReadOnlyList<long> segmentStartMs, IEnumerable<long> capturedMs)` - unchanged; it takes any capture times, not only note times.
- Produces: no new API surface. `GET /api/recordings/{id}` no longer merges two same-speaker segments when a screenshot sits between them.

**Why this matters:** without it, a screenshot captured between two same-speaker segments that later merge would anchor after the whole merged block, appearing noticeably later in the transcript than it was taken. The in-memory provider will not translate this faithfully, so the test belongs in the integration project.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/ScreenshotMergeBreakTests.cs`, following the existing integration tests' use of `ContainersFixture` for the DbContext and controller construction:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class ScreenshotMergeBreakTests
{
    private readonly ContainersFixture _fx;
    public ScreenshotMergeBreakTests(ContainersFixture fx) => _fx = fx;

    [Fact]
    public async Task Get_KeepsSameSpeakerSegmentsSeparate_WhenAScreenshotSitsBetweenThem()
    {
        await using var db = _fx.CreateDbContext();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var transcriptionId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId}@t.test", UserName = $"{userId}@t.test" });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Version = 1 });
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 5_000, Original = "first", Ordinal = 0 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = "SPEAKER_00", StartMs = 6_000, EndMs = 9_000, Original = "second", Ordinal = 1 });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecordingId = recordingId,
            CapturedAtMs = 5_500, // between the two same-speaker segments
            BlobKey = "s.png",
            ThumbBlobKey = "s.thumb.jpg",
            SizeBytes = 10,
        });
        await db.SaveChangesAsync();

        var controller = _fx.CreateRecordingsController(db, userId);
        var detail = (await controller.Get(recordingId)).Value!;

        Assert.Equal(2, detail.Current!.Segments.Count);
        Assert.Equal("first", detail.Current.Segments[0].Text);
        Assert.Equal("second", detail.Current.Segments[1].Text);
    }
}
```

If the fixture has no `CreateRecordingsController` helper, construct the controller exactly as the neighbouring integration tests do (note the second construction site in `RbacIntegrationTests.cs` when the constructor signature changes).

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ScreenshotMergeBreak"
```

Expected: FAIL - one merged segment reading "first second" instead of two.

- [ ] **Step 3: Add screenshot times to the break set**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, replace the note-times block with:

```csharp
        // A note or a screenshot sits between two segments; don't let a same-speaker merge swallow that
        // boundary (the note or image would jump to after the whole merged block). Flag the segment after
        // each anchor. Both kinds of capture use the same rule, so they share one break set.
        var noteTimes = await _db.MeetingNotes
            .Where(n => n.RecordingId == id && n.CapturedAtMs != null)
            .Select(n => n.CapturedAtMs!.Value)
            .ToListAsync();
        var shotTimes = await _db.MeetingScreenshots
            .Where(s => s.RecordingId == id)
            .Select(s => s.CapturedAtMs)
            .ToListAsync();
        var breakBefore = TranscriptNoteAnchor.BreakBeforeIndices(
            segments.Select(s => s.StartMs).ToList(), noteTimes.Concat(shotTimes));
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ScreenshotMergeBreak"
```

Expected: PASS.

- [ ] **Step 5: Build the solution and commit**

```bash
dotnet build Diariz.slnx
git add src/Diariz.Api tests/Diariz.Api.IntegrationTests
git commit -m "fix(api): keep same-speaker segments apart around a screenshot"
```

Expected: build succeeds with no warnings introduced.

---

## Task 10: Web types and API client

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/lib/api.ts`

**Interfaces:**
- Consumes: the routes from Task 7.
- Produces:
  - `Screenshot { id: string; capturedAtMs: number; width: number; height: number; sizeBytes: number; ordinal: number; createdAt: string }`.
  - `api.listScreenshots(recordingId): Promise<Screenshot[]>`
  - `api.createScreenshot(recordingId, shot: { capturedAtMs: number; width: number; height: number; full: Blob; thumb: Blob }): Promise<Screenshot>`
  - `api.deleteScreenshot(recordingId, screenshotId): Promise<void>`
  - `api.screenshotContentUrl(recordingId, screenshotId): string`
  - `api.screenshotThumbUrl(recordingId, screenshotId): string`

This task has no test of its own - it is a thin transport layer, exercised by Tasks 13-16. Type errors are caught by `npm run build`.

- [ ] **Step 1: Add the type**

In `apps/web/src/lib/types.ts`, after the `MeetingNote` interface:

```ts
/// A screen capture taken during a recording (desktop client only). capturedAtMs is the offset into the
/// recording clock; immutable after capture. Image bytes come from the content and thumb URLs.
export interface Screenshot {
  id: string;
  capturedAtMs: number;
  width: number;
  height: number;
  sizeBytes: number;
  ordinal: number;
  createdAt: string;
}
```

- [ ] **Step 2: Add the client methods**

In `apps/web/src/lib/api.ts`, add `Screenshot` to the type import list, then add this block after the meeting-notes section:

```ts
  // ---- Meeting screenshots (captures taken during a recording) ----

  async listScreenshots(recordingId: string): Promise<Screenshot[]> {
    const { data } = await http.get<Screenshot[]>(`/api/recordings/${recordingId}/screenshots`);
    return data;
  },

  /// Upload one capture (full PNG plus JPEG thumbnail) with the clock offset it was taken at.
  async createScreenshot(
    recordingId: string,
    shot: { capturedAtMs: number; width: number; height: number; full: Blob; thumb: Blob },
  ): Promise<Screenshot> {
    const form = new FormData();
    form.append("full", shot.full, "screenshot.png");
    form.append("thumb", shot.thumb, "screenshot.thumb.jpg");
    form.append("capturedAtMs", String(shot.capturedAtMs));
    form.append("width", String(shot.width));
    form.append("height", String(shot.height));
    const { data } = await http.post<Screenshot>(`/api/recordings/${recordingId}/screenshots`, form);
    return data;
  },

  async deleteScreenshot(recordingId: string, screenshotId: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/screenshots/${screenshotId}`);
  },

  /// Self-authenticating image URLs. An <img> request can't set an Authorization header, so the
  /// bearer rides along as `access_token` - the same mechanism attachmentContentUrl uses.
  screenshotContentUrl(recordingId: string, screenshotId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/recordings/${recordingId}/screenshots/${screenshotId}/content?access_token=${token}`;
  },

  screenshotThumbUrl(recordingId: string, screenshotId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/recordings/${recordingId}/screenshots/${screenshotId}/thumb?access_token=${token}`;
  },
```

- [ ] **Step 3: Typecheck and commit**

```bash
cd apps/web && npm run build
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts
git commit -m "feat(web): screenshot types and API client methods"
```

Expected: build succeeds.

---

## Task 11: Durable stash for captures taken before upload

**Files:**
- Create: `apps/web/src/lib/pendingScreenshots.ts`
- Test: `apps/web/src/lib/pendingScreenshots.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `PendingShot { capturedAtMs: number; width: number; height: number; full: Blob; thumb: Blob }`
  - `PendingScreenshots { userId: string; shots: PendingShot[]; recordingId: string | null; updatedAt: number }`
  - `savePendingScreenshots(value): Promise<void>`, `loadPendingScreenshots(userId): Promise<PendingScreenshots | null>`, `clearPendingScreenshots(userId): Promise<void>`. All degrade to no-ops without IndexedDB.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/pendingScreenshots.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import {
  savePendingScreenshots,
  loadPendingScreenshots,
  clearPendingScreenshots,
  type PendingScreenshots,
} from "./pendingScreenshots";

const stash = (userId: string): PendingScreenshots => ({
  userId,
  recordingId: null,
  updatedAt: 1,
  shots: [
    {
      capturedAtMs: 5_000,
      width: 1920,
      height: 1080,
      full: new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }),
      thumb: new Blob([new Uint8Array([4])], { type: "image/jpeg" }),
    },
  ],
});

describe("pendingScreenshots", () => {
  it("returns null when nothing is stashed for the user", async () => {
    expect(await loadPendingScreenshots("nobody")).toBeNull();
  });

  it("round-trips a stash including its image blobs", async () => {
    await savePendingScreenshots(stash("u1"));

    const loaded = await loadPendingScreenshots("u1");

    expect(loaded?.shots).toHaveLength(1);
    expect(loaded?.shots[0].capturedAtMs).toBe(5_000);
    expect(loaded?.shots[0].width).toBe(1920);
    expect(await loaded!.shots[0].full.arrayBuffer()).toEqual(new Uint8Array([1, 2, 3]).buffer);
  });

  it("keeps each user's stash separate", async () => {
    await savePendingScreenshots({ ...stash("u2"), recordingId: "rec-2" });

    expect((await loadPendingScreenshots("u2"))?.recordingId).toBe("rec-2");
    expect(await loadPendingScreenshots("u3")).toBeNull();
  });

  it("clears a stash", async () => {
    await savePendingScreenshots(stash("u4"));

    await clearPendingScreenshots("u4");

    expect(await loadPendingScreenshots("u4")).toBeNull();
  });
});
```

This needs a fake IndexedDB in jsdom. Install it as a dev dependency and register it in the test setup:

```bash
cd apps/web && npm install --save-dev fake-indexeddb
```

Add to `apps/web/src/test-setup.ts`:

```ts
import "fake-indexeddb/auto";
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/lib/pendingScreenshots.test.ts
```

Expected: FAIL - cannot resolve `./pendingScreenshots`.

- [ ] **Step 3: Write the implementation**

Create `apps/web/src/lib/pendingScreenshots.ts`:

```ts
/// Durable stash for screenshots captured during a recording, so a crash or a session lapse never loses
/// them. Mirrors pendingNotes, in its own database (`diariz-screenshots`) - adding a store to an existing
/// DB would force a version bump across modules. Keyed by user id. `recordingId` is null while the
/// recording is in progress; it is set when the audio uploaded but the screenshot attach failed, so the
/// retry path knows where the captures belong. Blobs are stored directly: IndexedDB handles them natively,
/// so no base64 inflation. All operations degrade to no-ops without IndexedDB.

export interface PendingShot {
  capturedAtMs: number;
  width: number;
  height: number;
  full: Blob;
  thumb: Blob;
}

export interface PendingScreenshots {
  userId: string;
  shots: PendingShot[];
  /// Null while recording; the created recording's id once audio uploaded but the attach failed.
  recordingId: string | null;
  updatedAt: number;
}

const DB_NAME = "diariz-screenshots";
const STORE = "pending-screenshots";

function openDb(): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains(STORE)) {
        req.result.createObjectStore(STORE, { keyPath: "userId" });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function withStore<T>(
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<T | null> {
  const dbp = openDb();
  if (!dbp) return null;
  try {
    const db = await dbp;
    return await new Promise<T | null>((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const req = fn(tx.objectStore(STORE));
      req.onsuccess = () => resolve((req.result as T) ?? null);
      req.onerror = () => reject(req.error);
    });
  } catch {
    return null; // best-effort: never let a storage hiccup break capturing
  }
}

export async function savePendingScreenshots(value: PendingScreenshots): Promise<void> {
  await withStore("readwrite", (s) => s.put(value));
}

export async function loadPendingScreenshots(userId: string): Promise<PendingScreenshots | null> {
  return withStore<PendingScreenshots>("readonly", (s) => s.get(userId));
}

export async function clearPendingScreenshots(userId: string): Promise<void> {
  await withStore("readwrite", (s) => s.delete(userId));
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/web && npx vitest run src/lib/pendingScreenshots.test.ts
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/pendingScreenshots.ts apps/web/src/lib/pendingScreenshots.test.ts apps/web/src/test-setup.ts apps/web/package.json apps/web/package-lock.json
git commit -m "feat(web): durable stash for captures taken before upload"
```

---

## Task 12: Weave screenshots into the transcript

**Files:**
- Modify: `apps/web/src/lib/transcriptNotes.ts`
- Modify: `apps/web/src/lib/transcriptView.test.ts` is unrelated; the tests to update are in `apps/web/src/lib/transcriptNotes.test.ts`

**Interfaces:**
- Consumes: nothing new.
- Produces: `weaveTranscript<S, N, P>(segments: S[], notes: N[], shots?: P[]): WovenRow<S, N, P>[]` where
  - `S extends { startMs: number }`, `N extends { capturedAtMs: number | null }`, `P extends { capturedAtMs: number }`
  - `WovenRow` gains a third variant `{ kind: "screenshot"; shot: P }`
  - Within one anchor, notes and screenshots are ordered together by `capturedAtMs`.
  - `anchorIndex` is unchanged. The third argument is optional, so the existing single-call site keeps working until Task 14 updates it.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/lib/transcriptNotes.test.ts`:

```ts
import { weaveTranscript } from "./transcriptNotes";

describe("weaveTranscript with screenshots", () => {
  const segments = [
    { startMs: 0, id: "s0" },
    { startMs: 10_000, id: "s1" },
  ];

  it("anchors a screenshot after the segment that was being spoken", () => {
    const rows = weaveTranscript(segments, [], [{ capturedAtMs: 4_000, id: "p0" }]);

    expect(rows.map((r) => r.kind)).toEqual(["segment", "screenshot", "segment"]);
  });

  it("puts a screenshot taken before the first segment at the very top", () => {
    const rows = weaveTranscript(segments, [], [{ capturedAtMs: 0, id: "p0" }]);

    expect(rows[0].kind).toBe("segment");
    expect(rows[1].kind).toBe("screenshot");
  });

  it("orders notes and screenshots sharing an anchor by capture time", () => {
    const rows = weaveTranscript(
      segments,
      [{ capturedAtMs: 5_000, id: "n0" }],
      [{ capturedAtMs: 3_000, id: "p0" }],
    );

    expect(rows.map((r) => r.kind)).toEqual(["segment", "screenshot", "note", "segment"]);
  });

  it("ignores screenshots when none are passed", () => {
    const rows = weaveTranscript(segments, [{ capturedAtMs: 1_000, id: "n0" }]);

    expect(rows.map((r) => r.kind)).toEqual(["segment", "note", "segment"]);
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npx vitest run src/lib/transcriptNotes.test.ts
```

Expected: FAIL - `weaveTranscript` takes two arguments and never emits a `screenshot` row.

- [ ] **Step 3: Rewrite the weaver**

Replace the body of `apps/web/src/lib/transcriptNotes.ts` below `anchorIndex` with:

```ts
export type WovenRow<S, N, P> =
  | { kind: "segment"; seg: S; index: number }
  | { kind: "note"; note: N }
  | { kind: "screenshot"; shot: P };

/// Interleave `segments` with the timed items captured during the meeting - the note-taker's notes and any
/// screenshots - into one ordered list of rows. Only notes with a `capturedAtMs` are woven in (pre-meeting
/// notes have no place on the timeline); screenshots always have one. Items sharing an anchor are ordered
/// by capture time regardless of kind, so a note and a screenshot taken seconds apart read in the order
/// they happened. Each segment row carries its original index so the caller can key playback highlight off
/// the segment position rather than the woven position.
export function weaveTranscript<
  S extends { startMs: number },
  N extends { capturedAtMs: number | null },
  P extends { capturedAtMs: number },
>(segments: S[], notes: N[], shots: P[] = []): WovenRow<S, N, P>[] {
  const starts = segments.map((s) => s.startMs);

  type Timed = { at: number; row: WovenRow<S, N, P> };
  const byAnchor = new Map<number, Timed[]>();
  const add = (at: number, row: WovenRow<S, N, P>) => {
    const idx = anchorIndex(starts, at);
    const bucket = byAnchor.get(idx);
    if (bucket) bucket.push({ at, row });
    else byAnchor.set(idx, [{ at, row }]);
  };

  for (const note of notes) {
    if (note.capturedAtMs == null) continue;
    add(note.capturedAtMs, { kind: "note", note });
  }
  for (const shot of shots) add(shot.capturedAtMs, { kind: "screenshot", shot });

  const sorted = (idx: number) =>
    (byAnchor.get(idx) ?? [])
      .slice()
      .sort((a, b) => a.at - b.at)
      .map((t) => t.row);

  const rows: WovenRow<S, N, P>[] = [];
  for (const row of sorted(-1)) rows.push(row);
  segments.forEach((seg, index) => {
    rows.push({ kind: "segment", seg, index });
    for (const row of sorted(index)) rows.push(row);
  });
  return rows;
}
```

Note the sort must be stable for equal times - `Array.prototype.sort` is stable in every engine the app targets, so items captured in the same millisecond keep insertion order (notes before screenshots).

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/lib/transcriptNotes.test.ts
```

Expected: PASS - the new tests plus every pre-existing test in the file.

- [ ] **Step 5: Typecheck and commit**

```bash
cd apps/web && npm run build
git add apps/web/src/lib/transcriptNotes.ts apps/web/src/lib/transcriptNotes.test.ts
git commit -m "feat(web): weave screenshots into the transcript alongside notes"
```

---

## Task 13: Recorder captures, stashes and attaches

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx`
- Create: `apps/web/src/lib/trayScreenshots.ts`
- Test: `apps/web/src/lib/trayScreenshots.test.ts`

**Interfaces:**
- Consumes: `window.diariz.onScreenshotCaptured` / `captureScreenshot` / `changeCaptureArea` / `canCaptureScreenshot` (Task 4); `pendingScreenshots` (Task 11); `api.createScreenshot` (Task 10); the Recorder's existing `timing.elapsedMs(timingRef.current, Date.now())`.
- Produces:
  - `apps/web/src/lib/trayScreenshots.ts`: `onScreenshotCaptured(cb: (shot: CapturedShot) => void): () => void`, `requestCapture(): void`, `requestChangeArea(): void`, `canCaptureScreenshots(): boolean`, and `CapturedShot { full: Blob; thumb: Blob; width: number; height: number }`. This is the seam that makes the Recorder testable in jsdom, mirroring `lib/trayRecorder.ts`.
  - Recorder behaviour: a capture while recording is stamped, stashed and shown; after a successful upload all stashed captures are POSTed; a failure keeps the stash with the recording id.

- [ ] **Step 1: Write the failing test for the bridge**

Create `apps/web/src/lib/trayScreenshots.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  canCaptureScreenshots,
  onScreenshotCaptured,
  requestCapture,
  requestChangeArea,
} from "./trayScreenshots";

declare global {
  interface Window {
    diariz?: unknown;
  }
}

afterEach(() => {
  delete window.diariz;
});

describe("trayScreenshots", () => {
  it("reports no capture support in a plain browser", () => {
    expect(canCaptureScreenshots()).toBe(false);
  });

  it("reports capture support when the shell exposes it", () => {
    window.diariz = { canCaptureScreenshot: true };

    expect(canCaptureScreenshots()).toBe(true);
  });

  it("subscribing without a shell is a no-op that still returns an unsubscribe", () => {
    const unsubscribe = onScreenshotCaptured(() => {});

    expect(() => unsubscribe()).not.toThrow();
  });

  it("converts the shell's ArrayBuffers into typed image blobs", async () => {
    let emit: ((payload: unknown) => void) | null = null;
    window.diariz = {
      canCaptureScreenshot: true,
      onScreenshotCaptured: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {};
      },
    };
    const seen: { full: Blob; thumb: Blob; width: number; height: number }[] = [];
    onScreenshotCaptured((shot) => seen.push(shot));

    emit!({ full: new Uint8Array([1, 2]).buffer, thumb: new Uint8Array([3]).buffer, width: 800, height: 600 });

    expect(seen).toHaveLength(1);
    expect(seen[0].full.type).toBe("image/png");
    expect(seen[0].thumb.type).toBe("image/jpeg");
    expect(seen[0].width).toBe(800);
  });

  it("requesting a capture without a shell does not throw", () => {
    expect(() => requestCapture()).not.toThrow();
    expect(() => requestChangeArea()).not.toThrow();
  });

  it("forwards a capture request to the shell", () => {
    const captureScreenshot = vi.fn();
    const changeCaptureArea = vi.fn();
    window.diariz = { canCaptureScreenshot: true, captureScreenshot, changeCaptureArea };

    requestCapture();
    requestChangeArea();

    expect(captureScreenshot).toHaveBeenCalledOnce();
    expect(changeCaptureArea).toHaveBeenCalledOnce();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/lib/trayScreenshots.test.ts
```

Expected: FAIL - cannot resolve `./trayScreenshots`.

- [ ] **Step 3: Write the bridge**

Create `apps/web/src/lib/trayScreenshots.ts`:

```ts
/// The seam between the Electron shell's screen capture and the web recorder. Mirrors trayRecorder: the
/// shell owns the capture (hotkey, tray, capture area), the web app owns the recording clock. Everything
/// here is a no-op in a plain browser, so callers never have to branch on `isElectron`.

export interface CapturedShot {
  full: Blob;
  thumb: Blob;
  width: number;
  height: number;
}

interface ShellPayload {
  full: ArrayBuffer;
  thumb: ArrayBuffer;
  width: number;
  height: number;
}

interface ScreenshotShell {
  canCaptureScreenshot?: boolean;
  captureScreenshot?: () => void;
  changeCaptureArea?: () => void;
  onScreenshotCaptured?: (cb: (payload: ShellPayload) => void) => () => void;
}

function shell(): ScreenshotShell | undefined {
  return (window as { diariz?: ScreenshotShell }).diariz;
}

/// True when this build can capture screenshots (the desktop shell). Drives whether the UI shows any
/// capture affordance at all.
export function canCaptureScreenshots(): boolean {
  return shell()?.canCaptureScreenshot === true;
}

/// Subscribe to captures from the shell. Returns an unsubscribe function (a no-op in a browser).
export function onScreenshotCaptured(cb: (shot: CapturedShot) => void): () => void {
  const api = shell();
  if (!api?.onScreenshotCaptured) return () => {};
  return api.onScreenshotCaptured((payload) =>
    cb({
      full: new Blob([payload.full], { type: "image/png" }),
      thumb: new Blob([payload.thumb], { type: "image/jpeg" }),
      width: payload.width,
      height: payload.height,
    }),
  );
}

export function requestCapture(): void {
  shell()?.captureScreenshot?.();
}

export function requestChangeArea(): void {
  shell()?.changeCaptureArea?.();
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/web && npx vitest run src/lib/trayScreenshots.test.ts
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Wire the Recorder**

In `apps/web/src/components/Recorder.tsx`:

Add the imports:

```ts
import { canCaptureScreenshots, onScreenshotCaptured, requestChangeArea, type CapturedShot } from "../lib/trayScreenshots";
import {
  savePendingScreenshots,
  loadPendingScreenshots,
  clearPendingScreenshots,
  type PendingScreenshots,
  type PendingShot,
} from "../lib/pendingScreenshots";
```

Add state and a ref next to `liveLines` / `liveLinesRef`:

```ts
  // Screenshots captured while recording: stamped with the *recorded* clock, mirrored to IndexedDB so a
  // crash never loses them, and attached to the recording after upload (exactly like live notes).
  const [liveShots, setLiveShots] = useState<PendingShot[]>([]);
  const liveShotsRef = useRef<PendingShot[]>([]);
  // Captures whose audio uploaded but whose attach failed - drives the retry banner.
  const [shotsAttach, setShotsAttach] = useState<PendingScreenshots | null>(null);
```

Extend the mount effect that loads `loadPendingNotes` to also load the screenshot stash:

```ts
    void loadPendingScreenshots(userId).then((stash) => {
      if (!cancelled && stash && stash.shots.length > 0 && stash.recordingId) setShotsAttach(stash);
    });
```

Add the capture handlers next to the live-notes handlers:

```ts
  /// Update the captures and mirror them to IndexedDB (recordingId null = still recording).
  function mirrorShots(shots: PendingShot[]) {
    liveShotsRef.current = shots;
    setLiveShots(shots);
    if (userId) void savePendingScreenshots({ userId, recordingId: null, updatedAt: Date.now(), shots });
  }

  function addLiveShot(shot: CapturedShot) {
    mirrorShots([
      ...liveShotsRef.current,
      {
        capturedAtMs: timing.elapsedMs(timingRef.current, Date.now()),
        width: shot.width,
        height: shot.height,
        full: shot.full,
        thumb: shot.thumb,
      },
    ]);
  }

  function deleteLiveShot(index: number) {
    mirrorShots(liveShotsRef.current.filter((_, i) => i !== index));
  }

  /// Attach captures to the created recording. Success clears the durable stash; failure keeps them (with
  /// the recording id) and surfaces the retry banner. A screenshot failure never fails the upload itself.
  async function attachScreenshots(recordingId: string, fromRetry?: PendingScreenshots) {
    const shots = fromRetry ? fromRetry.shots : liveShotsRef.current;
    if (shots.length === 0) {
      if (userId) void clearPendingScreenshots(userId);
      return;
    }
    try {
      for (const shot of shots) await api.createScreenshot(recordingId, shot);
      if (userId) await clearPendingScreenshots(userId);
      liveShotsRef.current = [];
      setLiveShots([]);
      setShotsAttach(null);
    } catch {
      const stash: PendingScreenshots = { userId: userId ?? "", recordingId, shots, updatedAt: Date.now() };
      if (userId) await savePendingScreenshots(stash);
      setShotsAttach(stash);
    }
  }
```

Subscribe to shell captures - only while recording, so a stray hotkey outside a meeting cannot enqueue anything:

```ts
  // Captures arrive from the Electron shell; the renderer stamps them with the recording clock because it
  // is the only side that knows about pauses.
  useEffect(() => {
    if (!canCaptureScreenshots()) return;
    return onScreenshotCaptured((shot) => {
      if (timingRef.current.runningSince === null && timingRef.current.accumulatedMs === 0) return;
      addLiveShot(shot);
    });
  }, []);
```

Finally, call `attachScreenshots(recordingId)` immediately after the existing `attachNotes(recordingId)` call in the upload path, and add a retry alongside the notes retry banner that calls `attachScreenshots(shotsAttach.recordingId!, shotsAttach)`.

- [ ] **Step 6: Verify the existing Recorder tests still pass**

```bash
cd apps/web && npx vitest run src/components/Recorder.test.tsx
```

Expected: PASS - no behaviour change for non-Electron rendering.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib/trayScreenshots.ts apps/web/src/lib/trayScreenshots.test.ts apps/web/src/components/Recorder.tsx
git commit -m "feat(web): stamp, stash and attach captures taken during a recording"
```

---

## Task 14: Screenshot modal and thumbnail strip

**Files:**
- Create: `apps/web/src/components/ScreenshotModal.tsx`
- Create: `apps/web/src/components/ScreenshotStrip.tsx`
- Test: `apps/web/src/components/ScreenshotModal.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: `Screenshot` (Task 10); `api.screenshotContentUrl`, `api.screenshotThumbUrl`, `api.deleteScreenshot`.
- Produces:
  - `<ScreenshotModal recordingId shots index onIndexChange onClose onJump onDelete />` where `shots: Screenshot[]`, `index: number`, `onJump?: (ms: number) => void`, `onDelete?: (id: string) => void`.
  - `<ScreenshotStrip recordingId shots onOpen />` where `onOpen: (index: number) => void`.

- [ ] **Step 1: Add the strings**

Add to `apps/web/src/locales/en/workspace.json` (and the same keys, translated, to `de`, `es`, `fr`):

```json
  "screenshots": "Screenshots",
  "screenshotsEmpty": "No screenshots captured",
  "screenshotAlt": "Screenshot at {{time}}",
  "screenshotPrev": "Previous screenshot",
  "screenshotNext": "Next screenshot",
  "screenshotClose": "Close screenshot",
  "screenshotJump": "Jump to {{time}}",
  "screenshotDownload": "Download screenshot",
  "screenshotDelete": "Delete screenshot",
  "screenshotCaptureArea": "Change capture area"
```

Keep every value free of em and en dashes.

- [ ] **Step 2: Write the failing test**

Create `apps/web/src/components/ScreenshotModal.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import ScreenshotModal from "./ScreenshotModal";
import type { Screenshot } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    screenshotContentUrl: (r: string, s: string) => `/content/${r}/${s}`,
    screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}`,
    deleteScreenshot: vi.fn().mockResolvedValue(undefined),
  },
}));

const shots: Screenshot[] = [
  { id: "a", capturedAtMs: 65_000, width: 100, height: 50, sizeBytes: 1, ordinal: 0, createdAt: "" },
  { id: "b", capturedAtMs: 125_000, width: 100, height: 50, sizeBytes: 1, ordinal: 1, createdAt: "" },
];

describe("ScreenshotModal", () => {
  it("shows the selected capture's full image", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.getByRole("img")).toHaveAttribute("src", "/content/r1/a");
  });

  it("moves to the next capture", async () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={onIndexChange} onClose={() => {}} />);

    await userEvent.click(screen.getByRole("button", { name: /next screenshot/i }));

    expect(onIndexChange).toHaveBeenCalledWith(1);
  });

  it("wraps around from the last capture to the first", async () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={onIndexChange} onClose={() => {}} />);

    await userEvent.click(screen.getByRole("button", { name: /next screenshot/i }));

    expect(onIndexChange).toHaveBeenCalledWith(0);
  });

  it("jumps playback to the moment the capture was taken", async () => {
    const onJump = vi.fn();
    render(
      <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} onJump={onJump} />,
    );

    await userEvent.click(screen.getByRole("button", { name: /jump to 1:05/i }));

    expect(onJump).toHaveBeenCalledWith(65_000);
  });

  it("closes on Escape", async () => {
    const onClose = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />);

    await userEvent.keyboard("{Escape}");

    expect(onClose).toHaveBeenCalled();
  });
});
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/ScreenshotModal.test.tsx
```

Expected: FAIL - cannot resolve `./ScreenshotModal`.

- [ ] **Step 4: Write the components**

Create `apps/web/src/components/ScreenshotStrip.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Screenshot } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// A row of capture thumbnails. Used both in the live recorder popover (immediate feedback that the
/// capture area is right) and in the Notes tab's collapsed section.
export default function ScreenshotStrip({
  recordingId,
  shots,
  onOpen,
}: {
  recordingId: string;
  shots: Screenshot[];
  onOpen: (index: number) => void;
}) {
  const { t } = useTranslation("workspace");

  if (shots.length === 0)
    return <p className="text-xs text-gray-400 dark:text-gray-500">{t("screenshotsEmpty")}</p>;

  return (
    <ul className="flex flex-wrap gap-2">
      {shots.map((shot, i) => (
        <li key={shot.id}>
          <button
            type="button"
            onClick={() => onOpen(i)}
            aria-label={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
            className="block overflow-hidden rounded border hover:ring-2 hover:ring-blue-400 dark:border-gray-700"
          >
            <img
              src={api.screenshotThumbUrl(recordingId, shot.id)}
              alt={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
              loading="lazy"
              className="h-20 w-auto"
            />
          </button>
        </li>
      ))}
    </ul>
  );
}
```

Create `apps/web/src/components/ScreenshotModal.tsx`:

```tsx
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Screenshot } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// Full-size viewer for one capture, with prev/next through the recording's captures, a jump to the
/// moment it was taken, download and delete. Index is owned by the caller so the transcript row, the
/// Notes section and the modal all agree on which capture is open.
export default function ScreenshotModal({
  recordingId,
  shots,
  index,
  onIndexChange,
  onClose,
  onJump,
  onDelete,
}: {
  recordingId: string;
  shots: Screenshot[];
  index: number;
  onIndexChange: (index: number) => void;
  onClose: () => void;
  onJump?: (ms: number) => void;
  onDelete?: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const shot = shots[index];

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
      else if (e.key === "ArrowRight") onIndexChange((index + 1) % shots.length);
      else if (e.key === "ArrowLeft") onIndexChange((index - 1 + shots.length) % shots.length);
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [index, shots.length, onClose, onIndexChange]);

  if (!shot) return null;

  const btn =
    "rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div
      className="fixed inset-0 z-50 flex flex-col items-center justify-center bg-black/70 p-4"
      onClick={onClose}
      role="presentation"
    >
      <div
        className="flex max-h-full w-full max-w-5xl flex-col gap-2 rounded bg-white p-3 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" className={btn} aria-label={t("screenshotPrev")}
            onClick={() => onIndexChange((index - 1 + shots.length) % shots.length)}>
            ◀
          </button>
          <button type="button" className={btn} aria-label={t("screenshotNext")}
            onClick={() => onIndexChange((index + 1) % shots.length)}>
            ▶
          </button>
          {onJump && (
            <button type="button" className={btn}
              aria-label={t("screenshotJump", { time: fmt(shot.capturedAtMs) })}
              onClick={() => onJump(shot.capturedAtMs)}>
              {fmt(shot.capturedAtMs)}
            </button>
          )}
          <span className="flex-1" />
          <a className={btn} href={api.screenshotContentUrl(recordingId, shot.id)} download
            aria-label={t("screenshotDownload")}>
            ⤓
          </a>
          {onDelete && (
            <button type="button" aria-label={t("screenshotDelete")}
              className="rounded border border-red-300 px-2 py-1 text-sm text-red-600 hover:bg-red-50 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-950"
              onClick={() => onDelete(shot.id)}>
              ✕
            </button>
          )}
          <button type="button" className={btn} aria-label={t("screenshotClose")} onClick={onClose}>
            ✕
          </button>
        </div>
        <img
          src={api.screenshotContentUrl(recordingId, shot.id)}
          alt={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
          className="max-h-[75vh] w-auto self-center object-contain"
        />
      </div>
    </div>
  );
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
cd apps/web && npx vitest run src/components/ScreenshotModal.test.tsx
```

Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ScreenshotModal.tsx apps/web/src/components/ScreenshotStrip.tsx apps/web/src/components/ScreenshotModal.test.tsx apps/web/src/locales
git commit -m "feat(web): screenshot modal and thumbnail strip"
```

---

## Task 15: Notes tab section and transcript rows

**Files:**
- Create: `apps/web/src/components/ScreenshotsSection.tsx`
- Test: `apps/web/src/components/ScreenshotsSection.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: `ScreenshotStrip`, `ScreenshotModal` (Task 14); `weaveTranscript` third argument (Task 12); `api.listScreenshots`, `api.deleteScreenshot` (Task 10).
- Produces: `<ScreenshotsSection recordingId shots onOpen />` - a `<details>` block, **collapsed by default**, labelled `Screenshots (n)`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/ScreenshotsSection.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import ScreenshotsSection from "./ScreenshotsSection";
import type { Screenshot } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}` },
}));

const shots: Screenshot[] = [
  { id: "a", capturedAtMs: 1_000, width: 10, height: 10, sizeBytes: 1, ordinal: 0, createdAt: "" },
  { id: "b", capturedAtMs: 2_000, width: 10, height: 10, sizeBytes: 1, ordinal: 1, createdAt: "" },
];

describe("ScreenshotsSection", () => {
  it("is collapsed by default", () => {
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(screen.getByRole("group")).not.toHaveAttribute("open");
  });

  it("shows the capture count in its label", () => {
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(screen.getByText(/Screenshots \(2\)/)).toBeInTheDocument();
  });

  it("renders nothing when the recording has no captures", () => {
    const { container } = render(<ScreenshotsSection recordingId="r1" shots={[]} onOpen={() => {}} />);

    expect(container).toBeEmptyDOMElement();
  });

  it("opens a capture when its thumbnail is clicked", async () => {
    const onOpen = vi.fn();
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={onOpen} />);

    // jsdom keeps a closed <details>'s children in the DOM, so the thumbnails are
    // queryable without toggling it open first. The buttons are the thumbnails only -
    // <summary> is not a button.
    await userEvent.click(screen.getAllByRole("button")[0]);

    expect(onOpen).toHaveBeenCalledWith(0);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/ScreenshotsSection.test.tsx
```

Expected: FAIL - cannot resolve `./ScreenshotsSection`.

- [ ] **Step 3: Write the component**

Create `apps/web/src/components/ScreenshotsSection.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import ScreenshotStrip from "./ScreenshotStrip";
import type { Screenshot } from "../lib/types";

/// The Notes tab's screenshot block. Collapsed by default so the note lines stay the focus, and hidden
/// entirely when the recording has no captures (most recordings won't).
export default function ScreenshotsSection({
  recordingId,
  shots,
  onOpen,
}: {
  recordingId: string;
  shots: Screenshot[];
  onOpen: (index: number) => void;
}) {
  const { t } = useTranslation("workspace");
  if (shots.length === 0) return null;

  return (
    <details className="rounded border p-2 dark:border-gray-700">
      <summary className="cursor-pointer text-sm font-medium dark:text-gray-200">
        {t("screenshots")} ({shots.length})
      </summary>
      <div className="pt-2">
        <ScreenshotStrip recordingId={recordingId} shots={shots} onOpen={onOpen} />
      </div>
    </details>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/web && npx vitest run src/components/ScreenshotsSection.test.tsx
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Wire the detail page**

In `apps/web/src/pages/RecordingDetail.tsx`:

Add the imports:

```ts
import ScreenshotModal from "../components/ScreenshotModal";
import ScreenshotsSection from "../components/ScreenshotsSection";
```

Add the query next to the existing notes query (line 136):

```ts
  // Captures taken during the recording; woven into the transcript and listed in the Notes tab.
  const { data: shots = [], refetch: refetchShots } = useQuery({
    queryKey: ["screenshots", id],
    queryFn: () => api.listScreenshots(id),
  });
  const [openShot, setOpenShot] = useState<number | null>(null);

  async function removeShot(shotId: string) {
    await api.deleteScreenshot(id, shotId);
    setOpenShot(null);
    await refetchShots();
  }
```

Pass screenshots to the weaver and render the row (replace the `weaveTranscript(rec.current.segments, notes)` call at line 1398):

```tsx
            {weaveTranscript(rec.current.segments, notes, shots).map((row) =>
              row.kind === "note" ? (
                <NoteRow key={`note-${row.note.id}`} note={row.note} speaker={fullName ?? email ?? t("workspace:noteSpeakerYou")} />
              ) : row.kind === "screenshot" ? (
                <li key={`shot-${row.shot.id}`} className="flex items-start gap-2">
                  <button
                    type="button"
                    onClick={() => setOpenShot(shots.findIndex((s) => s.id === row.shot.id))}
                    className="overflow-hidden rounded border hover:ring-2 hover:ring-blue-400 dark:border-gray-700"
                    aria-label={t("workspace:screenshotAlt", { time: formatMs(row.shot.capturedAtMs) })}
                  >
                    <img
                      src={api.screenshotThumbUrl(id, row.shot.id)}
                      alt={t("workspace:screenshotAlt", { time: formatMs(row.shot.capturedAtMs) })}
                      loading="lazy"
                      className="h-24 w-auto"
                    />
                  </button>
                </li>
              ) : (
                <SegmentRow
                  key={row.seg.id}
                  seg={row.seg}
                  speakerName={multiSpeakerLabels.has(row.seg.speaker) ? t("workspace:multipleSpeakers") : row.seg.speakerDisplay}
                  assign={segmentAssign}
                  active={row.index === activeIdx}
                  selected={selectedSegIds.has(row.seg.id)}
                  selectMode={selectMode}
                  showOriginal={showOriginal}
                  onClick={() => clickSegment(row.seg.id)}
                />
              ),
            )}
```

Use whatever mm:ss helper this file already has in place of `formatMs` if it is named differently.

Add the section to the Notes tab content, directly below the `<NotesSection .../>` element (line 1216):

```tsx
          <ScreenshotsSection recordingId={id} shots={shots} onOpen={setOpenShot} />
```

Render the modal once, next to the page's other modals:

```tsx
      {openShot !== null && (
        <ScreenshotModal
          recordingId={id}
          shots={shots}
          index={openShot}
          onIndexChange={setOpenShot}
          onClose={() => setOpenShot(null)}
          onJump={jumpToMs}
          onDelete={removeShot}
        />
      )}
```

- [ ] **Step 6: Run the web suite**

```bash
cd apps/web && npm test && npm run build
```

Expected: all tests pass, build succeeds.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/ScreenshotsSection.tsx apps/web/src/components/ScreenshotsSection.test.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "feat(web): screenshots in the transcript and the Notes tab"
```

---

## Task 16: Live feedback in the recorder popover

**Files:**
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`
- Modify: `apps/web/src/components/Recorder.tsx`
- Test: `apps/web/src/components/hub/NotesPopover.test.tsx` (create if absent)

**Interfaces:**
- Consumes: `liveShots` / `deleteLiveShot` (Task 13); `requestChangeArea`, `canCaptureScreenshots` (Task 13).
- Produces: `NotesPopover` gains props `shots: PendingShot[]`, `onDeleteShot: (index: number) => void`, `onChangeCaptureArea?: () => void`. The strip renders from local object URLs, since these captures have no server id yet.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/hub/NotesPopover.test.tsx` (if the file exists, add these cases to it, matching its existing render helper and required props):

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import NotesPopover from "./NotesPopover";
import type { PendingShot } from "../../lib/pendingScreenshots";

const shot = (capturedAtMs: number): PendingShot => ({
  capturedAtMs,
  width: 10,
  height: 10,
  full: new Blob(["f"], { type: "image/png" }),
  thumb: new Blob(["t"], { type: "image/jpeg" }),
});

describe("NotesPopover screenshots", () => {
  it("shows one thumbnail per capture taken so far", () => {
    render(
      <NotesPopover notes={[]} shots={[shot(1_000), shot(2_000)]} onAdd={() => {}} onEdit={() => {}}
        onDelete={() => {}} onDeleteShot={() => {}} onClose={() => {}} onChangeCaptureArea={() => {}} />,
    );

    expect(screen.getAllByRole("img")).toHaveLength(2);
  });

  it("offers changing the capture area", async () => {
    const onChangeCaptureArea = vi.fn();
    render(
      <NotesPopover notes={[]} shots={[shot(1_000)]} onAdd={() => {}} onEdit={() => {}} onDelete={() => {}}
        onDeleteShot={() => {}} onClose={() => {}} onChangeCaptureArea={onChangeCaptureArea} />,
    );

    await userEvent.click(screen.getByRole("button", { name: /change capture area/i }));

    expect(onChangeCaptureArea).toHaveBeenCalledOnce();
  });

  it("shows no screenshot area when the shell cannot capture", () => {
    render(
      <NotesPopover notes={[]} shots={[]} onAdd={() => {}} onEdit={() => {}} onDelete={() => {}}
        onDeleteShot={() => {}} onClose={() => {}} />,
    );

    expect(screen.queryByRole("button", { name: /change capture area/i })).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/hub/NotesPopover.test.tsx
```

Expected: FAIL - `NotesPopover` does not accept `shots`, and no thumbnails render.

- [ ] **Step 3: Extend the popover**

In `apps/web/src/components/hub/NotesPopover.tsx`, add the props and render a strip below the note list. Object URLs are created per render pass and revoked on cleanup so the popover never leaks memory across a long meeting:

```tsx
import { useEffect, useMemo } from "react";
import type { PendingShot } from "../../lib/pendingScreenshots";
```

Add to the component's props:

```tsx
  shots: PendingShot[];
  onDeleteShot: (index: number) => void;
  /// Absent in a plain browser, which is what hides the whole screenshot area.
  onChangeCaptureArea?: () => void;
```

Add inside the component, before the return:

```tsx
  // Local previews for captures that have no server id yet. Revoked when the set changes so a long
  // meeting doesn't accumulate object URLs.
  const previews = useMemo(() => shots.map((s) => URL.createObjectURL(s.thumb)), [shots]);
  useEffect(() => () => previews.forEach((url) => URL.revokeObjectURL(url)), [previews]);
```

Add this block below the existing notes list markup:

```tsx
      {onChangeCaptureArea && (
        <div className="space-y-1 border-t pt-2 dark:border-gray-700">
          <div className="flex items-center justify-between">
            <span className="text-xs font-medium dark:text-gray-300">
              {t("screenshots")} ({shots.length})
            </span>
            <button
              type="button"
              onClick={onChangeCaptureArea}
              className="rounded border px-1.5 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("screenshotCaptureArea")}
            </button>
          </div>
          <ul className="flex flex-wrap gap-1">
            {previews.map((url, i) => (
              <li key={url} className="relative">
                <img src={url} alt={t("screenshotAlt", { time: fmt(shots[i].capturedAtMs) })} className="h-14 w-auto rounded border dark:border-gray-700" />
                <button
                  type="button"
                  aria-label={t("screenshotDelete")}
                  onClick={() => onDeleteShot(i)}
                  className="absolute -right-1 -top-1 rounded-full bg-white px-1 text-xs text-red-600 shadow dark:bg-gray-900 dark:text-red-400"
                >
                  ✕
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
```

If the file has no `fmt` helper, add the same three-line one used in `ScreenshotStrip.tsx`.

- [ ] **Step 4: Pass the props from the Recorder**

At the `<NotesPopover ... />` call site in `apps/web/src/components/Recorder.tsx`, add:

```tsx
          shots={liveShots}
          onDeleteShot={deleteLiveShot}
          onChangeCaptureArea={canCaptureScreenshots() ? requestChangeArea : undefined}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd apps/web && npm test
```

Expected: PASS, including the new popover tests.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/hub/NotesPopover.tsx apps/web/src/components/hub/NotesPopover.test.tsx apps/web/src/components/Recorder.tsx
git commit -m "feat(web): live screenshot feedback in the recorder popover"
```

---

## Task 17: Release, docs and PR

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`

**Interfaces:** none - documentation and release metadata.

This is a **functional enhancement**, so the version bumps **Minor +1, Build reset to 0**: from `0.145.1` to `0.146.0`.

- [ ] **Step 1: Bump the version in all four places**

Set `"version": "0.146.0"` in `version.json`, `apps/web/package.json` and `apps/desktop/package.json`, and `<Version>0.146.0</Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 2: Add the release entry**

Add to the top of `RELEASES` in `apps/web/src/lib/releases.ts`, using the real PR number once the PR exists:

```ts
  {
    version: "0.146.0",
    date: "2026-07-22",
    pr: 0, // replace with the real PR number
    headline: "Capture screenshots during a meeting",
    summary:
      "The Windows desktop app can now capture the screen while you record. The first capture in a meeting asks which screen or which rectangle to use, and every later capture reuses it. Screenshots appear in the transcript at the moment they were taken, as thumbnails you can click to view full size, and the Notes tab lists them in a collapsed Screenshots section.",
    added: [
      "Capture a screenshot while recording, from a configurable global hotkey, the tray menu, or the app",
      "Choose a whole screen or a rectangle on the first capture of each meeting, and change it mid-meeting",
      "Screenshots appear inline in the transcript at their captured time, with a full-size viewer",
      "A collapsed Screenshots section in the Notes tab lists a recording's captures",
    ],
    changed: [
      "Screenshot images count toward your storage quota, alongside recordings and attachments",
    ],
    fixed: [
      "A note or screenshot between two turns by the same speaker no longer lets those turns merge past it",
    ],
  },
```

Add a `CAPABILITIES` row in the same file:

```
| Meeting screenshots | Capture the screen during a recording from the desktop app; captures appear in the transcript at the moment they were taken. |
```

- [ ] **Step 3: Update the README, features and reference docs**

- `README.md` Features table: add a row matching the `CAPABILITIES` wording.
- `docs/features.md`: add the full prose bullet describing capture, the area picker, transcript placement, the modal and the Notes section.
- `docs/Overall_Synopsis_of_Platform.md`: document the desktop capture contract (main owns capture and the area, the renderer owns the clock; the `screenshot:capture`, `screenshot:change-area`, `screenshot:captured`, `picker:choose`, `picker:cancel`, `hotkey:load` and `hotkey:save` channels), the new `api/recordings/{id}/screenshots` endpoints, and the `?access_token=` image URLs.
- `docs/Data_Schema.md`: add the `MeetingScreenshots` table with every column, the `(RecordingId, CapturedAtMs)` index, the two cascade FKs, the migration-history row for `AddMeetingScreenshots`, and the MinIO key layout `{userId}/screenshots/{id}.png` and `{id}.thumb.jpg`.

- [ ] **Step 4: Verify the release assertion and full suites**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS - `releases.test.ts` asserts `RELEASES[0].version` equals `version.json`.

```bash
dotnet build Diariz.slnx && dotnet test
```

Expected: PASS (integration tests need Docker).

```bash
cd apps/desktop && npm test
```

Expected: PASS.

- [ ] **Step 5: Commit and open the PR**

```bash
git add -A
git commit -m "chore: release 0.146.0 - meeting screenshots"
git push -u origin feat/meeting-screenshots
```

Open the PR with `gh pr create`. The description must state the deployment surface explicitly:

> **Deployment surface:** this needs a **desktop release** (cut a `v*` tag for the Windows installer) **and** a server redeploy. The shell changes (`apps/desktop/src/**`) only reach users through a new installer; the web and API changes ship with the redeploy.

Then update the `pr:` field in `releases.ts` with the real number and push that follow-up commit.

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
|---|---|
| Hotkey, tray item, in-app trigger | 4, 5 |
| Configurable hotkey | 5 |
| First capture defines screen or rectangle | 3, 4 |
| Later captures reuse the target | 4 |
| Target resets per recording | 4 (Step 3) |
| Change capture area mid-recording | 4, 16 |
| Full PNG capped at 2560, JPEG thumbnail | 1, 4 |
| Durable stash before upload | 11, 13 |
| Attach after upload, with retry | 13 |
| `MeetingScreenshot` entity and migration | 6 |
| Endpoints incl. `?access_token=` images | 7 |
| Quota accounting | 7, 8 |
| Blob cleanup on recording delete | 8 |
| Audio-retention deletion leaves screenshots | 8 (no change needed - that path only touches the audio blob) |
| Backup and restore, no format bump | 6 (Step 6 note) |
| Transcript weaving | 12, 15 |
| Merge-break union | 9 |
| Modal with prev/next, jump, download, delete | 14 |
| Collapsed Notes-tab section | 15 |
| Live strip in the recorder popover | 16 |
| Desktop-only affordances | 13, 16 |
| Release checklist items 1-7 | 17 |

**Deferred items** (OCR, summariser input, annotation, browser capture, window targets) are recorded in the spec's "Out of scope" section and are deliberately absent from this plan.

**Known follow-up:** `docs/Data_Schema.md` and `Overall_Synopsis_of_Platform.md` edits in Task 17 are described rather than shown, because their exact insertion points depend on the state of those files at implementation time. Every code step shows its code.
