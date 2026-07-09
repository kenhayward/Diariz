# Combined Microphone + System Audio Recording - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user on a single device record **their microphone and the system audio at the same time**,
mixed into one track, so a remote laptop captures both sides of a call for transcription.

**Architecture:** The mic dropdown stays as-is (the **default** is unchanged - a microphone). A new
**"System audio" checkbox** beside it, when ticked, additionally captures system audio (`getDisplayMedia`)
and mixes it with the mic (`getUserMedia`) via the Web Audio API into one
`MediaStreamAudioDestinationNode`, which feeds the existing `MediaRecorder` -> WebM -> upload path. No
worker/pipeline changes: pyannote still diarizes the mixed track into per-speaker segments. A new
`RecordingSource.Combined` tags the upload. The checkbox is **hidden where system audio is unsupported**
(Firefox/Safari); it is available in Chromium browsers and the desktop app.

**Tech Stack:** React 19 + TS (Vite), Web Audio API (`AudioContext`, `MediaStreamAudioSourceNode`,
`MediaStreamAudioDestinationNode`), `MediaRecorder`; ASP.NET Core enum; Electron tray (Node). vitest +
node --test + xUnit.

---

## Correction: system audio already works in the browser

My earlier assessment ("system audio is desktop-only") was **wrong**, and the user is right.
`apps/web/src/lib/audioSource.ts` `getSystemStream()` already calls
`navigator.mediaDevices.getDisplayMedia({ video: true, audio: true })` - the standard browser API. In a
Chromium browser (Chrome/Edge) the screen-share dialog offers a **"Share audio"** checkbox; ticking it
returns a real system/tab audio track and recording works. What the app does today is only *cosmetic
over-caution*:

- `buildSourceOptions` appends `systemDesktopSuffix` (" (desktop only)") to the System label when not in
  Electron (`Recorder.tsx:537`).
- `describeAudioError` returns "System audio capture needs the desktop app." for `source === "system" &&
  !electron` (`audioSource.ts:100`).

**Browser support matrix (reality):**

| Environment | System / Combined audio |
|---|---|
| Electron desktop shell | Yes - loopback handler auto-grants, no visible picker, whole-system, cross-platform (`apps/desktop/src/main.js:60`) |
| Chrome / Edge (Chromium) desktop | Yes - via the screen-share dialog's "Share audio" (tab always; whole-screen/system on Windows/ChromeOS) |
| Firefox | No - `getDisplayMedia` returns **no audio track** |
| Safari | No - no `getDisplayMedia` audio |

So this plan **stops gating system audio as desktop-only** and instead **degrades gracefully**: show the
"System audio" checkbox wherever `getDisplayMedia` exists, and when no audio track comes back (the user
didn't tick "Share audio", or cancelled), fall back to microphone-only for that take with a non-fatal
notice.

## `getDisplayMedia` requests video by design

We keep `getDisplayMedia({ video: true, audio: true })` even though we only want audio: **audio-only display
capture is not supported** - Chromium throws `NotSupportedError` if `video` is omitted (per the spec, "it is
not currently possible to capture only audio"). `getSystemStream()` already stops and discards the video
track in the same tick and returns an audio-only `MediaStream`, so nothing extra is captured or uploaded. No
change here; this is the correct call.

## UI model: mic dropdown (incl. "No Microphone") + "System audio" checkbox

- **Dropdown:** `No Microphone` + `Microphone (default)` + specific mics. The **default selection is
  unchanged** - `Microphone (default)`.
- **"System audio" checkbox** beside the dropdown, shown **only when `supportsDisplayAudio()`** is true
  (hidden in Firefox/Safari, shown in Chromium + the desktop app). Its state persists in localStorage.
- **Record is enabled only when at least one source is active** - i.e. NOT (`No Microphone` selected AND the
  System audio checkbox off). This is the one new guard.
- **Effective capture (all four cases covered):**

  | Dropdown | System audio checkbox | Capture | `source` |
  |---|---|---|---|
  | a mic | off | mic only | `Microphone` |
  | a mic | on | mic + system mixed | `Combined` |
  | No Microphone | on | system only | `System` |
  | No Microphone | off | *(Record disabled)* | - |

- **Graceful fallback:** the screen-share picker shows only when system audio is requested. If the user
  cancels/doesn't tick "Share audio" (no system track): when a **mic is also selected**, record **mic-only**
  with a non-fatal notice (`combinedFellBackToMic`); when it's **system-only** (No Microphone), there's
  nothing to fall back to, so surface the normal capture error. In the desktop shell the loopback handler
  auto-grants (no visible picker).

## Echo

The mic's `echoCancellation` stays **on** (it already is - `DEFAULT_CONSTRAINTS.echoCancellation = true`),
which cancels most far-end bleed. **No headphone hint** in the UI (per request - it would nag).

---

## File Structure

**Backend**
- Modify: `src/Diariz.Domain/Entities/RecordingSource.cs` - add `Combined = 3`.
- Test: `tests/Diariz.Api.Tests/RecordingsControllerTests.cs` - upload with `source=Combined` persists it.

**Web - pure logic (unit-testable, no browser APIs)**
- Modify: `apps/web/src/lib/audioDevices.ts` - add a `"none"` (No Microphone) `SourceKind` + dropdown
  option; `buildSourceOptions` drops the standalone "System audio" entry (system moves to the checkbox);
  `SourceOptionLabels` gains `noMic`, loses `system`.
- Test: `apps/web/src/lib/audioDevices.test.ts`.
- Modify: `apps/web/src/lib/types.ts` - `RecordingSource` union gains `"Combined"`.

**Web - capture (thin browser wrapper)**
- Modify: `apps/web/src/lib/audioSource.ts` - `CaptureSession` type, `getCombinedStream`, `getStream`
  returns a session, `supportsDisplayAudio()`, updated `describeAudioError`, `AudioSourceKind` gains
  `"both"`.
- Test: `apps/web/src/lib/audioSource.test.ts` - support detection + error messaging (mix itself is
  covered by the Recorder test's mocks + manual smoke).

**Web - component**
- Modify: `apps/web/src/components/Recorder.tsx` - `systemAudio` checkbox (shown only when supported,
  persisted), session teardown, combined capture with mic-only fallback, upload `source="Combined"` when
  ticked.
- Test: `apps/web/src/components/Recorder.test.tsx`.

**Web - i18n**
- Modify: `apps/web/src/locales/{en,es,fr,de}/workspace.json` - `systemAudioToggle`, `sourceCombined`,
  `recTitlePrefixBoth`, `combinedFellBackToMic`. (No echo/headphone string.)

**Desktop tray**
- Modify: `apps/desktop/src/recorderState.js` - `"both"` source label + a "Record Both" tray item.
- Test: `apps/desktop/src/recorderState.test.js` (node --test).
- Modify: `apps/desktop/src/main.js` - map the new `record-both` tray id to a `start("both")` command.

**Docs / versioning (single release)**
- `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts` (new entry + CAPABILITIES row),
  `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`.

Deployment surface: **desktop release** (tray model + `main.js` change) **and** server redeploy (web + API).

---

## Task 1: Backend `RecordingSource.Combined`

**Files:**
- Modify: `src/Diariz.Domain/Entities/RecordingSource.cs`
- Test: `tests/Diariz.Api.Tests/RecordingsControllerTests.cs`

- [ ] **Step 1: Write the failing test** (append near the existing upload tests)

```csharp
[Fact]
public async Task Upload_WithCombinedSource_PersistsCombined()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var controller = Build(db, userId, new FakeJobQueue(), new FakeAudioStorage());

    var result = await controller.Upload(
        FakeAudio(Encoding.UTF8.GetBytes("both-sides")), title: null, durationMs: 1000,
        source: RecordingSource.Combined);

    var summary = Ok(result);
    var rec = await db.Recordings.FirstAsync(r => r.Id == summary.Id);
    Assert.Equal(RecordingSource.Combined, rec.Source);
}
```

(Match the exact `Upload(...)` signature used by the neighbouring tests - see
`RecordingsControllerTests.cs` around line 243. `RecordingSource` binds from the multipart `source`
field via `[FromForm] RecordingSource source`, so no controller change is needed.)

- [ ] **Step 2: Run it - expect FAIL** (compile error: `Combined` doesn't exist)

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name=Upload_WithCombinedSource_PersistsCombined"`

- [ ] **Step 3: Add the enum member** (append-only - never renumber)

```csharp
// src/Diariz.Domain/Entities/RecordingSource.cs
public enum RecordingSource
{
    Microphone = 0,
    System = 1,
    Upload = 2,
    Combined = 3, // mic + system audio mixed into one track (single-device dual capture)
}
```

- [ ] **Step 4: Run it - expect PASS.**

- [ ] **Step 5: Commit.** `git commit -am "feat(domain): RecordingSource.Combined"`

---

## Task 2: "No Microphone" dropdown option + `RecordingSource` union

The dropdown gains a `No Microphone` (`kind: "none"`) option and drops the standalone "System audio" entry
(system moves to the checkbox in Task 4). `SourceOptionLabels` gains `noMic`, loses `system`. The
`AudioSourceKind` coarse type gains `"both"` (used by the tray + upload title). `getStream`'s existing
`system` branch is kept and reused for the system-only case.

**Files:**
- Modify: `apps/web/src/lib/audioDevices.ts`
- Modify: `apps/web/src/lib/types.ts`
- Test: `apps/web/src/lib/audioDevices.test.ts`

- [ ] **Step 1: Write/adjust the failing tests**

```ts
it("round-trips the no-microphone token", () => {
  expect(parseSourceToken("none")).toEqual({ kind: "none" });
  expect(formatSourceToken({ kind: "none" })).toBe("none");
});

it("lists mic default, devices, then No Microphone when system audio is available", () => {
  const opts = buildSourceOptions(
    [{ deviceId: "d1", label: "Yeti" }], true,
    { micDefault: "Microphone (default)", noMic: "No microphone", numbered: (n) => `Microphone ${n}` },
    { canSystemAudio: true },
  );
  expect(opts.map((o) => o.kind)).toEqual(["default", "device", "none"]); // no "system"
});

it("omits No Microphone when system audio is unavailable (else Record could never enable)", () => {
  const opts = buildSourceOptions([], false,
    { micDefault: "Microphone (default)", noMic: "No microphone", numbered: (n) => `Microphone ${n}` },
    { canSystemAudio: false });
  expect(opts.map((o) => o.kind)).toEqual(["default"]);
});
```

- [ ] **Step 2: Run - expect FAIL.** Run: `npx vitest run src/lib/audioDevices.test.ts`

- [ ] **Step 3: Implement in `audioDevices.ts`**

```ts
export type SourceKind = "default" | "device" | "system" | "none";
export type AudioSourceKind = "mic" | "system" | "both"; // coarse; re-exported from audioSource

export interface SourceOptionLabels {
  micDefault: string;
  noMic: string;
  numbered: (n: number) => string;      // no `system` - system audio is the checkbox now
}

export function parseSourceToken(value: string): SourceSelection {
  if (value === "none") return { kind: "none" };
  if (value === "system") return { kind: "system" };   // legacy/internal
  if (value.startsWith(DEVICE_PREFIX)) {
    const deviceId = value.slice(DEVICE_PREFIX.length);
    if (deviceId) return { kind: "device", deviceId };
  }
  return { kind: "default" };
}

export function formatSourceToken(sel: SourceSelection): string {
  if (sel.kind === "none") return "none";
  if (sel.kind === "system") return "system";
  if (sel.kind === "device" && sel.deviceId) return `${DEVICE_PREFIX}${sel.deviceId}`;
  return "default";
}

// Microphone (default), enumerated devices, then "No microphone" (only when system audio is available -
// otherwise No Microphone could leave Record permanently disabled). System audio is the checkbox, not here.
export function buildSourceOptions(devices, hasLabels, labels, opts?): SourceOption[] {
  const options: SourceOption[] = [{ token: "default", label: labels.micDefault, kind: "default" }];
  devices.forEach((d, i) => {
    const label = hasLabels && d.label ? d.label : labels.numbered(i + 1);
    options.push({ token: `${DEVICE_PREFIX}${d.deviceId}`, label, kind: "device" });
  });
  if (opts?.canSystemAudio) options.push({ token: "none", label: labels.noMic, kind: "none" });
  return options;
}
```

`resolvePersistedSource` returns non-device selections verbatim, so `none` passes through unchanged. Guard
the same way if a stale `none` is restored while system audio is unavailable - fall back to `default` (the
component can coerce `selection.kind === "none" && !canSystemAudio` to `{ kind: "default" }` on mount).

- [ ] **Step 4: Widen the `RecordingSource` union**

```ts
// types.ts
export type RecordingSource = "Microphone" | "System" | "Upload" | "Combined";
```

- [ ] **Step 5: Run the tests - expect PASS.**

- [ ] **Step 6: Commit.** `git commit -am "feat(web): 'No Microphone' source option; Combined in RecordingSource"`

---

## Task 3: Capture session + `getCombinedStream` + support detection

**Files:**
- Modify: `apps/web/src/lib/audioSource.ts`
- Test: `apps/web/src/lib/audioSource.test.ts`

A `CaptureSession` unifies teardown: mic/system stop their own tracks; combined stops **both** source
streams and closes the `AudioContext`.

- [ ] **Step 1: Write failing tests for the testable seams** (support detection + error copy)

```ts
// audioSource.test.ts
import { supportsDisplayAudio, describeAudioError } from "./audioSource";

describe("supportsDisplayAudio", () => {
  it("true when getDisplayMedia exists (Chromium/desktop), false otherwise", () => {
    expect(supportsDisplayAudio({ mediaDevices: { getDisplayMedia: () => {} } } as any)).toBe(true);
    expect(supportsDisplayAudio({ mediaDevices: {} } as any)).toBe(false);
    expect(supportsDisplayAudio({} as any)).toBe(false);
  });
});

describe("describeAudioError - system no longer hard-requires the desktop app", () => {
  it("guides toward 'Share audio' rather than claiming desktop-only", () => {
    const msg = describeAudioError({ name: "NotAllowedError" }, "system", /*electron*/ false);
    expect(msg.toLowerCase()).not.toContain("needs the desktop app");
  });
});
```

- [ ] **Step 2: Run - expect FAIL.** Run: `npx vitest run src/lib/audioSource.test.ts`

- [ ] **Step 3: Implement in `audioSource.ts`**

```ts
export type AudioSourceKind = "mic" | "system" | "both";

export interface CaptureSession {
  stream: MediaStream;   // the stream fed to MediaRecorder + tapped by the level meter
  stop: () => void;      // stop all underlying tracks and release any AudioContext
}

export function supportsDisplayAudio(nav: Navigator = navigator): boolean {
  return typeof nav?.mediaDevices?.getDisplayMedia === "function";
}

function trackSession(stream: MediaStream): CaptureSession {
  return { stream, stop: () => stream.getTracks().forEach((t) => t.stop()) };
}

// Mic + system mixed into one track via Web Audio.
export async function getCombinedStream(constraints?: MediaTrackConstraints): Promise<CaptureSession> {
  // Mic AEC stays ON (constraints already carry echoCancellation) to cut loopback bleed.
  const mic = await getMicStream(undefined, constraints);
  let sys: MediaStream;
  try {
    sys = await getSystemStream();               // may throw (cancelled) or return no audio track
  } catch (e) {
    mic.getTracks().forEach((t) => t.stop());
    throw e;
  }
  if (sys.getAudioTracks().length === 0) {        // browser didn't share audio -> caller falls back to mic
    sys.getTracks().forEach((t) => t.stop());
    mic.getTracks().forEach((t) => t.stop());
    throw new DOMException("No system audio track", "NotFoundError");
  }

  const ctx = new AudioContext();
  const dest = ctx.createMediaStreamDestination();
  if (constraints?.channelCount === 1) { dest.channelCount = 1; dest.channelCountMode = "explicit"; }
  ctx.createMediaStreamSource(mic).connect(dest);
  ctx.createMediaStreamSource(sys).connect(dest);

  return {
    stream: dest.stream,
    stop: () => {
      mic.getTracks().forEach((t) => t.stop());
      sys.getTracks().forEach((t) => t.stop());
      void ctx.close();
    },
  };
}

// getStream now returns a CaptureSession for uniform teardown.
export async function getStream(selection, constraints?): Promise<CaptureSession> {
  const mtc = constraints ? toMediaTrackConstraints(constraints) : undefined;
  if (selection.kind === "both") return getCombinedStream(mtc);
  if (selection.kind === "system") return trackSession(await getSystemStream());
  return trackSession(await getMicStream(selection.kind === "device" ? selection.deviceId : undefined, mtc));
}
```

Update `describeAudioError`: drop the blanket `source === "system" && !electron ->
"needs the desktop app"` line. Instead, for `NotAllowedError`/`NotFoundError` when `source` is `system`/`both`,
return a message like: "No system audio was shared. In the share dialog tick 'Share audio' (Chrome/Edge), or
use the desktop app." Keep the existing mic messages.

- [ ] **Step 4: Run - expect PASS.**

- [ ] **Step 5: Commit.** `git commit -am "feat(web): getCombinedStream + CaptureSession + support-aware errors"`

*Note:* the actual Web Audio mixing is exercised by the Recorder test's mocks (Task 4) and a manual smoke
test (final checklist); jsdom has no real `AudioContext`.

---

## Task 4: Wire the Recorder ("System audio" checkbox, teardown, mic-only fallback, upload source)

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx`
- Test: `apps/web/src/components/Recorder.test.tsx`

New state: `const [systemAudio, setSystemAudio] = useState(false)` persisted under
`diariz.recorder.systemAudio`; a ref (`systemAudioRef`) so `start()` reads the latest value. The checkbox
renders only when `supportsDisplayAudio() || isElectron`. Derived: `wantMic = selection.kind !== "none"`,
`wantSystem = systemAudio`. **Record is disabled unless `wantMic || wantSystem`.**

- [ ] **Step 1: Extend the test mocks + write failing tests.** Add to the test's setup a stub
`navigator.mediaDevices.getDisplayMedia` (returns a stream with one fake audio track) and a minimal
`AudioContext` mock (`createMediaStreamSource` -> `{ connect(){} }`, `createMediaStreamDestination` ->
`{ stream: <fake mixed stream>, channelCount: 2 }`, `close()`). Then assert:

```tsx
it("shows the System audio checkbox where supported, hidden otherwise", async () => {
  render(<Recorder onUploaded={() => {}} />);
  expect(await screen.findByLabelText(/system audio/i)).toBeInTheDocument();
});

it("records mic only by default (checkbox off) -> source Microphone", async () => {
  render(<Recorder onUploaded={() => {}} />);
  fireEvent.click(screen.getByRole("button", { name: /record/i }));
  await screen.findByText(/●/);
  fireEvent.click(screen.getByRole("button", { name: /stop/i }));
  await waitFor(() => expect(api.upload).toHaveBeenCalled());
  expect(vi.mocked(api.upload).mock.calls[0][3]).toBe("Microphone");
  expect(navigator.mediaDevices.getDisplayMedia).not.toHaveBeenCalled(); // no picker unless opted in
});

it("mixes system audio when a mic is selected and the checkbox is ticked -> source Combined", async () => {
  render(<Recorder onUploaded={() => {}} />);
  fireEvent.click(await screen.findByLabelText(/system audio/i));
  fireEvent.click(screen.getByRole("button", { name: /record/i }));
  await screen.findByText(/●/);
  fireEvent.click(screen.getByRole("button", { name: /stop/i }));
  await waitFor(() => expect(api.upload).toHaveBeenCalled());
  expect(vi.mocked(api.upload).mock.calls[0][3]).toBe("Combined"); // 4th arg = source
});

it("records system only when No Microphone + system audio -> source System", async () => {
  render(<Recorder onUploaded={() => {}} />);
  fireEvent.change(await screen.findByLabelText(/microphone/i), { target: { value: "none" } });
  fireEvent.click(screen.getByLabelText(/system audio/i));
  fireEvent.click(screen.getByRole("button", { name: /record/i }));
  await screen.findByText(/●/);
  fireEvent.click(screen.getByRole("button", { name: /stop/i }));
  await waitFor(() => expect(api.upload).toHaveBeenCalled());
  expect(vi.mocked(api.upload).mock.calls[0][3]).toBe("System");
  expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled(); // no mic captured
});

it("disables Record when No Microphone and system audio is off", async () => {
  render(<Recorder onUploaded={() => {}} />);
  fireEvent.change(await screen.findByLabelText(/microphone/i), { target: { value: "none" } });
  expect(screen.getByRole("button", { name: /record/i })).toBeDisabled();
});

it("falls back to microphone-only when system audio isn't shared", async () => {
  vi.mocked(navigator.mediaDevices.getDisplayMedia).mockRejectedValueOnce(
    new DOMException("cancelled", "NotAllowedError"));
  render(<Recorder onUploaded={() => {}} />);
  fireEvent.click(await screen.findByLabelText(/system audio/i));
  fireEvent.click(screen.getByRole("button", { name: /record/i }));
  await screen.findByText(/●/);                      // still recording (mic only)
  fireEvent.click(screen.getByRole("button", { name: /stop/i }));
  await waitFor(() => expect(api.upload).toHaveBeenCalled());
  expect(vi.mocked(api.upload).mock.calls[0][3]).toBe("Microphone");
  expect(screen.getByText(/microphone only/i)).toBeInTheDocument();
});
```

- [ ] **Step 2: Run - expect FAIL.** Run: `npx vitest run src/components/Recorder.test.tsx`

- [ ] **Step 3: Implement the wiring.**
  - Initial `selection` stays `{ kind: "default" }`; add the persisted `systemAudio` boolean + its ref.
  - Replace the `streamRef`-only teardown with a `sessionRef = useRef<CaptureSession | null>(null)`. Keep
    `streamRef.current = session.stream` for the level meter + `setCaptureEnabled` (mute) - both operate on
    the recorded stream (the mixed destination stream when combined), so pause/mute + meter work unchanged.
  - In `start()` decide capture from `wantMic`/`wantSystem` (tray commands map: `mic`->mic-only,
    `system`->system-only, `both`->mic+system; the on-screen button reads the current selection + checkbox):

    ```ts
    const mtc = toMediaTrackConstraints(constraints);
    let session: CaptureSession, coarse: AudioSourceKind;
    if (wantMic && wantSystem) {           // combined
      try { session = await getCombinedStream(mtc); coarse = "both"; }
      catch (e) {                          // share cancelled / no audio track -> fall back to mic only
        if (!isAbortish(e)) throw e;
        session = await getStream(selection, constraints); coarse = "mic";
        setNotice(t("combinedFellBackToMic"));
      }
    } else if (wantMic) {                   // mic only
      session = await getStream(selection, constraints); coarse = "mic";
    } else {                                // system only (No Microphone) - no mic fallback
      session = await getStream({ kind: "system" }, undefined); coarse = "system";
    }
    activeSourceRef.current = coarse;
    ```
    (`isAbortish` = DOMException name in `NotAllowedError`/`NotFoundError`/`AbortError`. A system-only
    failure is NOT caught here, so it surfaces via the existing `describeAudioError` path.)
  - `recorder.onstop`: `sessionRef.current?.stop(); sessionRef.current = null; streamRef.current = null;`.
  - `upload()`: source map = `activeSourceRef.current === "both" ? "Combined" : activeSourceRef.current ===
    "system" ? "System" : "Microphone"`; title prefix `recTitlePrefixBoth` / `recTitlePrefixSystem` /
    `recTitlePrefixMic`.
  - **Record button** `disabled={busy || (selection.kind === "none" && !systemAudio)}`.
  - `const canSystem = supportsDisplayAudio() || isElectron;` Pass `{ canSystemAudio: canSystem }` to
    `buildSourceOptions` (so "No Microphone" only appears when system audio is available) and render the
    checkbox only when `canSystem`. On mount, coerce a restored `{ kind: "none" }` to `{ kind: "default" }`
    when `!canSystem`.
  - Render the checkbox (label `systemAudioToggle`, e.g. "System audio") beside the source dropdown,
    disabled while `recording`. Persist on toggle. The mic DSP ⚙ popover is disabled when `selection.kind
    === "none"` (no mic to tune). **No echo hint.**

- [ ] **Step 4: Run - expect PASS** (and the existing Recorder tests still green - note the old "System
  audio" dropdown option is gone, so update any test asserting it).

- [ ] **Step 5: Commit.** `git commit -am "feat(web): 'System audio' checkbox mixes mic+system (Combined), mic-only fallback"`

---

## Task 5: Desktop tray - "Record Both"

**Files:**
- Modify: `apps/desktop/src/recorderState.js`
- Test: `apps/desktop/src/recorderState.test.js`
- Modify: `apps/desktop/src/main.js`

- [ ] **Step 1: Write failing tests** (node --test)

```js
const { trayRecorderItems, trayTooltip, notificationFor } = require("./recorderState");

test("idle offers Record Microphone, System Audio, and Both", () => {
  const items = trayRecorderItems({ phase: "idle", ready: true }, 0);
  assert.deepEqual(items.map((i) => i.id), ["record-mic", "record-system", "record-both"]);
  assert.ok(items.every((i) => i.enabled));
});

test("tooltip + notification name the combined source", () => {
  assert.equal(trayTooltip({ phase: "recording", source: "both" }),
    "Diariz — recording microphone + system audio");
  assert.deepEqual(
    notificationFor({ phase: "idle" }, { phase: "recording", source: "both" }),
    { title: "Diariz", body: "Recording microphone + system audio…" });
});
```

- [ ] **Step 2: Run - expect FAIL.** Run (from `apps/desktop`): `npm test`

- [ ] **Step 3: Implement** in `recorderState.js`: `sourceLabel("both") -> "microphone + system audio"`;
idle items `[{ id: "record-mic", label: "Record Microphone", enabled }, { id: "record-system", label:
"Record System Audio", enabled }, { id: "record-both", label: "Record Both", enabled }]`.

- [ ] **Step 4:** In `main.js`, keep `record-mic`/`record-system` handlers and add `record-both` ->
`{ type: "start", source: "both" }` over IPC.

- [ ] **Step 5: Run - expect PASS.**

- [ ] **Step 6: Commit.** `git commit -am "feat(desktop): 'Record Both' tray item"`

---

## Task 6: i18n (en, es, fr, de)

**Files:** `apps/web/src/locales/{en,es,fr,de}/workspace.json`

- [ ] Add to each locale (English shown; translate the rest, **plain hyphens only, no em/en dashes**):

```json
"sourceNoMic": "No microphone",
"systemAudioToggle": "System audio",
"sourceCombined": "Microphone + System",
"recTitlePrefixBoth": "Mic+System",
"combinedFellBackToMic": "System audio wasn't shared - recording microphone only."
```

Remove `systemDesktopSuffix` (no longer "desktop only"). Keep `sourceSystem` / `recTitlePrefixSystem` -
still used by the system-only path (source badge + title). Update `RecordingsPanel.tsx` `sourceLabel` to add
`"Combined"` -> `t("workspace:sourceCombined")` (keep the existing `"System"` case). **No echo/headphone
string.**

- [ ] Run `npx vitest run` (locale single-language gate + component tests) - expect PASS.
- [ ] **Commit.** `git commit -am "i18n: combined-source strings; drop desktop-only wording"`

---

## Task 7: Version, release notes, docs (one release)

**Bump rule:** functional enhancement -> Minor +1, Build 0 (e.g. `0.112.x` -> `0.113.0`). Update in lockstep:
`version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] Add a `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` (headline "Record microphone and system
  audio together"), with `added` bullets, and update the **Capture** row of `CAPABILITIES` to mention
  "record mic + system audio together on one device".
- [ ] Update the **Capture** row in `README.md` and the Capture bullet in `docs/features.md` (both note the
  new combined option and that system audio works in Chromium browsers via "Share audio", seamless in the
  desktop app).
- [ ] Update `docs/Overall_Synopsis_of_Platform.md` where it describes capture/system audio (the desktop
  shell is no longer the *only* way to get system audio; it is the seamless/cross-platform way). Note the
  new `RecordingSource.Combined = 3` in `docs/Data_Schema.md` (enum list).
- [ ] `releases.test.ts` asserts `RELEASES[0].version === version.json`. Run `npx vitest run
  src/lib/releases.test.ts`.
- [ ] **Commit.** `git commit -am "chore: release notes + docs for combined recording"`

State in the PR: needs **both** a desktop release (tray `main.js`/`recorderState.js` changed) and a server
redeploy (web + API).

---

## Final verification (manual smoke - do before opening the PR)

1. `docker compose up` (or dev servers). In **Chrome/Edge**, tick the **System audio** checkbox, press
   Record, tick **"Share audio"** in the picker, talk while a video/call plays. Stop -> confirm the
   transcript has **both** your voice and the system audio, diarized as separate speakers, and the recording
   is tagged Combined.
2. Repeat in the **desktop app** - confirm no visible screen-share picker (loopback auto-grants) and both
   sides captured.
3. **System-only:** select **No Microphone** + tick System audio, record a playing video -> confirm it
   transcribes the system audio (no mic), tagged `System`.
4. Select **No Microphone** with System audio **off** -> confirm **Record is disabled**.
5. With a mic + checkbox on, press Record and **cancel** the share dialog -> confirm it records **mic-only**
   with the fallback notice (not an error). (System-only + cancel -> a normal capture error, nothing to fall
   back to.)
6. Checkbox **off** with a mic -> confirm today's mic-only behaviour is unchanged and no screen-share prompt.
7. **Firefox:** confirm the System audio checkbox **and** the No Microphone option are both **hidden**
   (system audio unsupported), so the user can only record a mic - no dead-end.
8. Sanity (optional): combined on laptop speakers - confirm the far-end isn't badly echoed (mic AEC on) - no
   UI hint either way.

## Out of scope (note for later, don't build now)

- Stereo split (mic -> L, system -> R) for playback separation - WhisperX downmixes to mono, so not worth
  it yet.
- Per-source level balancing (`GainNode`) - start 1:1; add a slider only if real recordings need it.
