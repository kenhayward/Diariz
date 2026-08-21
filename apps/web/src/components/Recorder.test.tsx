import { render, screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";

// A JWT-shaped token whose payload decodes to { sub: "u1" } (used for the per-user pending key).
const TOKEN = `h.${btoa(JSON.stringify({ sub: "u1" }))}.s`;

vi.mock("../lib/api", () => ({
  api: {
    upload: vi.fn(), createNotes: vi.fn(), createScreenshot: vi.fn(),
    renameRecording: vi.fn(), putCalendarLink: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fb: string) => fb,
  getToken: () => TOKEN,
}));
vi.mock("../lib/pendingNotes", () => ({
  savePendingNotes: vi.fn().mockResolvedValue(undefined),
  loadPendingNotes: vi.fn().mockResolvedValue(null),
  clearPendingNotes: vi.fn().mockResolvedValue(undefined),
}));
vi.mock("../lib/pendingScreenshots", () => ({
  addPendingScreenshot: vi.fn().mockResolvedValue(undefined),
  loadPendingScreenshots: vi.fn().mockResolvedValue(null),
  removePendingScreenshot: vi.fn().mockResolvedValue(undefined),
  setPendingScreenshotsRecordingId: vi.fn().mockResolvedValue(undefined),
  clearPendingScreenshots: vi.fn().mockResolvedValue(undefined),
}));
vi.mock("../lib/uploadContext", () => ({ useUpload: () => ({ uploadFiles: vi.fn() }) }));
const setStatus = vi.fn();
vi.mock("../lib/status", () => ({ useStatus: () => ({ status: null, setStatus }) }));
// The recorder now consults the current room's permissions + placement. Default: full access, no folder.
const roomState = {
  can: (_p: number) => true,
  recordingSectionId: null as string | null,
  currentRoom: undefined as { id: string; isPersonal: boolean } | undefined,
};
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({
    can: (p: number) => roomState.can(p),
    currentRoom: roomState.currentRoom,
    rooms: [],
    permissions: 0,
    selectedSectionId: null,
    recordingSectionId: roomState.recordingSectionId,
    isLoading: false,
  }),
}));
// The recorder is rendered bare (no QueryClientProvider), so its settings hook is mocked rather than its
// underlying query. Tests that care flip `calendarSettings` before rendering.
const calendarSettings = { enabled: false, afterMinutes: 3, silenceSeconds: 30 };
vi.mock("../lib/calendarRecordingSettings", () => ({
  useCalendarRecordingSettings: () => calendarSettings,
}));
// What the (stubbed) silence watcher reports the room is doing. The extend prompt asks it whether anyone is
// still talking at the calendar's stop time, so tests that care flip this before rendering.
let silenceState = { heardSound: true, silentMs: 0 };
const silenceWatcher = {
  setPaused: vi.fn(),
  stop: vi.fn(),
  state: () => silenceState,
  onSilent: null as (() => void) | null,
};
vi.mock("../lib/silenceWatcher", () => ({
  startSilenceWatcher: vi.fn((_stream: MediaStream, _thresholdMs: number, onSilent: () => void) => {
    silenceWatcher.onSilent = onSilent;
    return silenceWatcher;
  }),
}));
vi.mock("../lib/audioSource", () => ({
  getStream: vi.fn(),
  getCombinedStream: vi.fn(),
  supportsDisplayAudio: vi.fn(() => true),
  isElectron: false,
  describeAudioError: () => "audio error",
  listInputDevices: vi.fn().mockResolvedValue({ devices: [], hasLabels: false }),
  micPermissionState: vi.fn().mockResolvedValue("granted"),
  unlockDeviceLabels: vi.fn().mockResolvedValue(undefined),
}));
vi.mock("../lib/pendingRecording", () => ({
  savePendingRecording: vi.fn().mockResolvedValue(undefined),
  loadPendingRecording: vi.fn().mockResolvedValue(null),
  clearPendingRecording: vi.fn().mockResolvedValue(undefined),
}));

import { api } from "../lib/api";
import {
  getStream, getCombinedStream, listInputDevices, micPermissionState, unlockDeviceLabels,
} from "../lib/audioSource";
import { loadPendingRecording, clearPendingRecording } from "../lib/pendingRecording";
import { savePendingNotes, clearPendingNotes } from "../lib/pendingNotes";
import {
  addPendingScreenshot,
  loadPendingScreenshots,
  removePendingScreenshot,
  setPendingScreenshotsRecordingId,
  clearPendingScreenshots,
} from "../lib/pendingScreenshots";
import type { PendingShot } from "../lib/pendingScreenshots";
import Recorder, { MAX_LIVE_SCREENSHOTS } from "./Recorder";
import { requestRecording } from "../lib/recordRequest";
import { startSilenceWatcher } from "../lib/silenceWatcher";
import { ToastProvider } from "../lib/toast";

// jsdom has no MediaRecorder; a minimal stub lets start() run without capturing real audio.
class FakeMediaRecorder {
  ondataavailable: ((e: unknown) => void) | null = null;
  onstop: (() => void) | null = null;
  state = "inactive";
  constructor(
    public stream: unknown,
    public opts: unknown,
  ) {}
  start() {
    this.state = "recording";
  }
  pause() {
    this.state = "paused";
  }
  resume() {
    this.state = "recording";
  }
  stop() {
    this.state = "inactive";
    this.onstop?.();
  }
}
(globalThis as unknown as { MediaRecorder: unknown }).MediaRecorder = FakeMediaRecorder;
const fakeStream = { getTracks: () => [], getAudioTracks: () => [], getVideoTracks: () => [] };
// getStream/getCombinedStream resolve to a CaptureSession ({ stream, stop }).
const fakeSession = { stream: fakeStream, stop: () => {} };

const pending = {
  userId: "u1",
  blob: new Blob(["audio"], { type: "audio/webm" }),
  title: "Mic 6/30/2026",
  durationMs: 2_700_000,
  source: "Microphone" as const,
  createdAt: Date.now(),
};

// Restore defaults after any test that changes them, so ordering can't leak room state.
afterEach(() => {
  roomState.can = () => true;
  roomState.recordingSectionId = null;
  roomState.currentRoom = undefined;
});

describe("Recorder recovery", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("offers an unsaved recording and uploads it on demand", async () => {
    (loadPendingRecording as Mock).mockResolvedValue(pending);
    (api.upload as Mock).mockResolvedValue({});
    const onUploaded = vi.fn();

    render(<Recorder onUploaded={onUploaded} />);

    const uploadBtn = await screen.findByRole("button", { name: /upload now/i });
    fireEvent.click(uploadBtn);

    await waitFor(() =>
      // A stash written before the wall-clock fields existed still uploads; the times are simply absent.
      expect(api.upload).toHaveBeenCalledWith(
        pending.blob, pending.title, pending.durationMs, "Microphone", null, null,
        { startedAt: undefined, endedAt: undefined },
      ),
    );
    await waitFor(() => expect(clearPendingRecording).toHaveBeenCalledWith("u1"));
    expect(onUploaded).toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull());
  });

  it("replays the stashed wall clock rather than stamping the recovery moment", async () => {
    // A recording recovered on a later visit still happened when it happened. Uploading it with "now" would
    // silently lose the meeting match that stashing it was meant to protect.
    const stashed = { ...pending, startedAt: 1_760_000_000_000, endedAt: 1_760_003_600_000 };
    (loadPendingRecording as Mock).mockResolvedValue(stashed);
    (api.upload as Mock).mockResolvedValue({});

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /upload now/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][6]).toEqual({
      startedAt: 1_760_000_000_000,
      endedAt: 1_760_003_600_000,
    });
  });

  it("attaches screenshots stashed during a failed take once the audio recovers", async () => {
    // A prior take's audio upload failed (e.g. an expired session) but captures had already been taken
    // and stashed with recordingId: null. uploadPending() must adopt them onto the recovered recording,
    // exactly as it already does for notes - not abandon them.
    (loadPendingRecording as Mock).mockResolvedValue(pending);
    (api.upload as Mock).mockResolvedValue({ id: "rec-recovered" });
    (api.createScreenshot as Mock).mockResolvedValue({});
    (loadPendingScreenshots as Mock).mockResolvedValue({
      userId: "u1",
      recordingId: null,
      updatedAt: Date.now(),
      shots: [{ id: "shot-1", capturedAtMs: 4200, width: 800, height: 600, full: new Blob(["a"]), thumb: new Blob(["b"]) }],
    });

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /upload now/i }));

    await waitFor(() =>
      expect(api.createScreenshot).toHaveBeenCalledWith(
        "rec-recovered",
        expect.objectContaining({ capturedAtMs: 4200, width: 800, height: 600 }),
      ),
    );
    // The recovered capture is removed as its own record once it lands, not re-saved as a whole set.
    await waitFor(() => expect(removePendingScreenshot).toHaveBeenCalledWith("u1", "shot-1"));
    await waitFor(() => expect(clearPendingScreenshots).toHaveBeenCalledWith("u1"));
  });

  it("does not show a false error banner when recovering a pending upload hits a storage hiccup in the attach step", async () => {
    // Milder sibling of the duplicate-offer bug (see "live screenshots" below): here setPending(null) has
    // already run by the time the attach/recovery step throws, so the audio can't be re-offered - but an
    // unguarded throw would still surface a spurious error banner for an upload that actually succeeded.
    (loadPendingRecording as Mock).mockResolvedValue(pending);
    (api.upload as Mock).mockResolvedValue({ id: "rec-recovered" });
    // The component calls loadPendingScreenshots both on mount (its own recovery-banner check, whose
    // `.then()` has no `.catch()`) and inside uploadPending's attach/recovery step (the one this test
    // targets) - let the mount call succeed and only reject the second, so the hiccup lands exactly where
    // FIX 2 guards it rather than tripping an unrelated unhandled rejection from the mount effect.
    let screenshotLoadCalls = 0;
    (loadPendingScreenshots as Mock).mockImplementation(() =>
      ++screenshotLoadCalls === 1 ? Promise.resolve(null) : Promise.reject(new Error("storage hiccup")),
    );
    try {
      const onUploaded = vi.fn();

      render(<Recorder onUploaded={onUploaded} />);
      fireEvent.click(await screen.findByRole("button", { name: /upload now/i }));

      await waitFor(() => expect(onUploaded).toHaveBeenCalled());
      expect(screen.queryByText(/upload failed/i)).toBeNull();
    } finally {
      (loadPendingScreenshots as Mock).mockResolvedValue(null);
    }
  });

  it("discards an unsaved recording after confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (loadPendingRecording as Mock).mockResolvedValue(pending);

    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /discard/i }));

    await waitFor(() => expect(clearPendingRecording).toHaveBeenCalledWith("u1"));
    expect(api.upload).not.toHaveBeenCalled();
  });

  it("shows nothing to recover when there is no pending recording", async () => {
    (loadPendingRecording as Mock).mockResolvedValue(null);
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });
    expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull();
    expect(screen.queryByTestId("recorder-popover")).toBeNull();
  });

  // The banners float below the top bar rather than sitting in its flow: the header is a fixed height,
  // so an in-flow banner grew it and pushed the page down.
  it("floats the unsaved-recording banner in a popover, out of the top bar's flow", async () => {
    (loadPendingRecording as Mock).mockResolvedValue(pending);
    render(<Recorder onUploaded={() => {}} />);

    const popover = await screen.findByTestId("recorder-popover");
    expect(popover.className).toContain("absolute");
    expect(within(popover).getByRole("button", { name: /upload now/i })).toBeTruthy();
  });
});

describe("Recorder transport controls", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  // The circular transport buttons (pause/resume/stop) are icon-only: the glyph carries the meaning, the
  // label lives on aria-label (and title) so screen readers + hover tooltips still name them. The record
  // hero is now a labelled pill (icon + "Start recording" text) but keeps the accessible name "Record".
  const iconOnly = (btn: HTMLElement) => btn.querySelector("svg") !== null && btn.textContent === "";

  it("renders Record (hero pill) and Upload (icon) with accessible names", async () => {
    render(<Recorder onUploaded={() => {}} />);
    expect(await screen.findByRole("button", { name: /^record$/i })).toBeTruthy();
    expect(iconOnly(screen.getByRole("button", { name: /^upload$/i }))).toBe(true);
  });

  it("renders Pause, Resume and Stop as icons with accessible names", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /^record$/i }));

    const pause = await screen.findByRole("button", { name: /^pause$/i });
    expect(iconOnly(pause)).toBe(true);
    expect(iconOnly(screen.getByRole("button", { name: /^stop$/i }))).toBe(true);

    fireEvent.click(pause);
    expect(iconOnly(await screen.findByRole("button", { name: /^resume$/i }))).toBe(true);
  });

  it("clears its running interval timers on unmount so none fire after teardown", async () => {
    // The elapsed ticker and the auto-stop schedule watcher are window.setInterval timers started when
    // recording begins. If they are not cleared on unmount they keep firing; once the test environment is
    // torn down, the next tick calls stop() -> window.clearInterval and throws "window is not defined",
    // which vitest reports as an unhandled error and fails the whole run.
    const clearSpy = vi.spyOn(window, "clearInterval");
    const { unmount } = render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /^record$/i }));
    await screen.findByRole("button", { name: /^stop$/i }); // recording -> both intervals are now running

    clearSpy.mockClear();
    unmount();

    // Unmount must clear the running intervals (nothing else clears an interval on unmount).
    expect(clearSpy).toHaveBeenCalled();
    clearSpy.mockRestore();
  });

  it("shows no toast when the user presses Stop themselves", async () => {
    // Regression guard: stop() now takes an optional StopReason, and RecordHero's Stop button ultimately
    // wires straight into a native onClick. If Recorder ever passed the bare `stop` function reference as
    // that handler again, the click's SyntheticEvent would land in the `reason` parameter and this test
    // would catch the resulting (bogus) toast - a manual stop is deliberately reason-less, the user knows.
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(
      <ToastProvider>
        <Recorder onUploaded={() => {}} />
      </ToastProvider>,
    );
    fireEvent.click(await screen.findByRole("button", { name: /^record$/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    // Assert against the toast region directly (not just a text match): a garbled StopReason still raises
    // a toast, just with an empty/untranslated body, which a text-content assertion alone would miss.
    expect(document.querySelector('[role="status"]')?.children.length).toBe(0);
  });

  it("disables Record and Upload without CreateRecording, explaining why", async () => {
    roomState.can = () => false;
    render(<Recorder onUploaded={() => {}} />);

    const rec = await screen.findByRole("button", { name: /^record$/i });
    expect((rec as HTMLButtonElement).disabled).toBe(true);
    expect(rec.getAttribute("title")).toMatch(/permission/i);

    const upload = screen.getByRole("button", { name: /^upload$/i });
    expect((upload as HTMLButtonElement).disabled).toBe(true);
    expect(upload.getAttribute("title")).toMatch(/permission/i);
  });
});

describe("Recorder source selection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
  });

  // The mic select, system-audio toggle and processing chips now live inside the Audio source popover,
  // opened from the "Audio source" chip. Most source assertions open it first.
  const openSource = async () =>
    fireEvent.click(await screen.findByRole("button", { name: /audio source/i }));

  it("opens the audio source popover from the chip", async () => {
    render(<Recorder onUploaded={() => {}} />);
    // Closed by default: no popover, no mic combobox in the DOM.
    expect(screen.queryByTestId("audio-source-popover")).toBeNull();
    expect(screen.queryByRole("combobox", { name: /^microphone$/i })).toBeNull();

    await openSource();

    expect(screen.getByTestId("audio-source-popover")).toBeTruthy();
    expect(screen.getByRole("combobox", { name: /^microphone$/i })).toBeTruthy();
  });

  it("lists Microphone (default), then specific mics, then No microphone (system available)", async () => {
    (listInputDevices as Mock).mockResolvedValue({
      devices: [
        { deviceId: "aaa", label: "Built-in Mic" },
        { deviceId: "bbb", label: "USB Headset" },
      ],
      hasLabels: true,
    });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    await screen.findByRole("option", { name: "USB Headset" });
    const select = screen.getByRole("combobox", { name: /^microphone$/i });
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual([
      "Microphone (default)",
      "Built-in Mic",
      "USB Headset",
      "No microphone",
    ]);
  });

  it("records the chosen specific mic with the current capture constraints", async () => {
    (listInputDevices as Mock).mockResolvedValue({
      devices: [{ deviceId: "bbb", label: "USB Headset" }],
      hasLabels: true,
    });
    (getStream as Mock).mockResolvedValue(fakeSession);
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    // Wait for the async device load to populate the options before selecting — otherwise a slow
    // runner can click Record before the label is known, resolving the source without its label.
    await screen.findByRole("option", { name: "USB Headset" });
    fireEvent.change(screen.getByRole("combobox", { name: /^microphone$/i }), { target: { value: "dev:bbb" } });
    fireEvent.click(screen.getByRole("button", { name: /record/i }));

    await waitFor(() =>
      expect(getStream).toHaveBeenCalledWith(
        { kind: "device", deviceId: "bbb", label: "USB Headset" },
        { echoCancellation: false, noiseSuppression: false, autoGainControl: false, mono: true },
      ),
    );
  });

  it("restores a persisted specific-mic choice on mount", async () => {
    localStorage.setItem(
      "diariz.recorder.source",
      JSON.stringify({ token: "dev:bbb", label: "USB Headset" }),
    );
    (listInputDevices as Mock).mockResolvedValue({
      devices: [{ deviceId: "bbb", label: "USB Headset" }],
      hasLabels: true,
    });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    const select = (await screen.findByRole("combobox", { name: /^microphone$/i })) as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe("dev:bbb"));
  });

  it("feeds processing-chip constraint changes into capture", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    render(<Recorder onUploaded={() => {}} />);

    await openSource();
    // The DSP now starts off, so ticking the chip turns noise suppression ON - the point of the test is
    // that a chip change reaches getStream, whichever way it is pointing.
    fireEvent.click(screen.getByRole("checkbox", { name: /noise suppression/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));

    await waitFor(() =>
      expect(getStream).toHaveBeenCalledWith(
        { kind: "default" },
        expect.objectContaining({ noiseSuppression: true }),
      ),
    );
  });

  it("disables the processing chips when No Microphone is selected", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.change(await screen.findByRole("combobox", { name: /^microphone$/i }), { target: { value: "none" } });
    expect((screen.getByRole("checkbox", { name: /mono/i }) as HTMLInputElement).disabled).toBe(true);
  });

  it("shows the System audio checkbox where supported", async () => {
    render(<Recorder onUploaded={() => {}} />);
    await openSource();
    expect(await screen.findByRole("checkbox", { name: /system audio/i })).toBeTruthy();
  });

  it("records mic only by default (checkbox off) -> source Microphone, no combined capture", async () => {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Microphone");
    expect(getCombinedStream).not.toHaveBeenCalled();
  });

  it("reports the wall clock it started and stopped, not just the recorded duration", async () => {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    const before = Date.now();
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    const times = (api.upload as Mock).mock.calls[0][6] as { startedAt: number; endedAt: number };
    expect(times.startedAt).toBeGreaterThanOrEqual(before);
    expect(times.endedAt).toBeGreaterThanOrEqual(times.startedAt);
  });

  it("keeps the original start across a pause, which Timing itself discards", async () => {
    // timing.pause() folds runningSince into accumulatedMs and nulls it, so the start instant is gone from
    // Timing by the time upload() runs. Written to fail if startedAt is ever derived from timingRef.
    //
    // Drives a virtual clock by stubbing Date.now (what the recorder actually reads) rather than installing
    // fake timers, which would freeze Testing Library's own polling and hang the test.
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    const t0 = 1_760_000_000_000;
    let clock = t0;
    const now = vi.spyOn(Date, "now").mockImplementation(() => clock);
    try {
      render(<Recorder onUploaded={() => {}} />);

      fireEvent.click(await screen.findByRole("button", { name: /record/i }));
      await screen.findByRole("button", { name: /^stop$/i });

      // Record 1 min, pause 20 min, record 1 more, then stop: 2 min of audio across a 22 min wall clock.
      clock = t0 + 60_000;
      fireEvent.click(screen.getByRole("button", { name: /^pause$/i }));
      clock = t0 + 21 * 60_000;
      fireEvent.click(screen.getByRole("button", { name: /^resume$/i }));
      clock = t0 + 22 * 60_000;
      fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

      await waitFor(() => expect(api.upload).toHaveBeenCalled());
      const call = (api.upload as Mock).mock.calls[0];
      const times = call[6] as { startedAt: number; endedAt: number };
      // The start is when Record was pressed, not when the last segment resumed.
      expect(times.startedAt).toBe(t0);
      // The span is the full 22 minutes, while durationMs stays the 2 minutes actually captured - which is
      // exactly why endedAt has to travel separately from durationMs.
      expect(times.endedAt - times.startedAt).toBe(22 * 60_000);
      expect(call[2]).toBe(2 * 60_000);
    } finally {
      now.mockRestore();
    }
  });

  it("files the recording into the folder resolved at Record time", async () => {
    roomState.recordingSectionId = "sec-42";
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][4]).toBe("sec-42"); // 5th arg = sectionId
  });

  it("recording in a shared room shares it there and keeps the main placement ungrouped", async () => {
    roomState.currentRoom = { id: "room-9", isPersonal: false };
    roomState.recordingSectionId = "sec-42"; // ignored for a shared room
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][4]).toBeNull(); // sectionId - ungrouped in the personal room
    expect((api.upload as Mock).mock.calls[0][5]).toBe("room-9"); // roomId
  });

  it("mixes system audio when a mic is selected and the checkbox is ticked -> source Combined", async () => {
    (getCombinedStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.click(await screen.findByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect(getCombinedStream).toHaveBeenCalled();
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Combined");
  });

  it("records system only when No Microphone + system audio -> source System", async () => {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.change(await screen.findByRole("combobox", { name: /^microphone$/i }), { target: { value: "none" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("System");
    expect(getStream).toHaveBeenCalledWith({ kind: "system" }, undefined);
    expect(getCombinedStream).not.toHaveBeenCalled();
  });

  it("disables Record when No Microphone and system audio is off", async () => {
    render(<Recorder onUploaded={() => {}} />);
    await openSource();
    fireEvent.change(await screen.findByRole("combobox", { name: /^microphone$/i }), { target: { value: "none" } });
    expect((screen.getByRole("button", { name: /record/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("falls back to microphone-only when system audio isn't shared", async () => {
    (getCombinedStream as Mock).mockRejectedValue(
      Object.assign(new Error("x"), { name: "NotAllowedError" }),
    );
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.click(await screen.findByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i }); // still recording (mic only)
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Microphone");
    // The notice goes to the status bar (amber), not inline in the top bar - an inline line there would
    // grow the fixed-height header and push it off screen.
    expect(screen.queryByText(/microphone only/i)).toBeNull();
    expect(setStatus).toHaveBeenCalledWith(expect.stringMatching(/microphone only/i), "progress", { sticky: true });
  });

  it("reports a capture failure to the status bar in the error tone", async () => {
    (getStream as Mock).mockRejectedValue(new Error("boom"));
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /^record$/i }));

    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith("audio error", "error", { sticky: true }),
    );
    expect(screen.queryByText("audio error")).toBeNull();
  });

  it("clears its status-bar message once the condition passes", async () => {
    (getStream as Mock).mockRejectedValueOnce(new Error("boom")).mockResolvedValue(fakeSession);
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /^record$/i }));
    await waitFor(() => expect(setStatus).toHaveBeenCalledWith("audio error", "error", { sticky: true }));

    setStatus.mockClear();
    fireEvent.click(screen.getByRole("button", { name: /^record$/i }));
    await waitFor(() => expect(setStatus).toHaveBeenCalledWith(null));
  });

  it("closes the audio source popover on a backdrop click", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);

    await openSource();
    expect(screen.getByRole("checkbox", { name: /mono/i })).toBeTruthy();

    fireEvent.click(screen.getByTestId("hub-popover-backdrop"));
    await waitFor(() => expect(screen.queryByRole("checkbox", { name: /mono/i })).toBeNull());
  });

  it("closes the audio source popover on Escape", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);

    await openSource();
    expect(screen.getByRole("checkbox", { name: /mono/i })).toBeTruthy();

    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("checkbox", { name: /mono/i })).toBeNull());
  });

  it("lists connected mics even when the permission query is not 'granted'", async () => {
    // navigator.permissions.query is unreliable (returns "prompt" in Electron even when labels are
    // available), so enumeration must not be gated on it.
    (micPermissionState as Mock).mockResolvedValue("prompt");
    (listInputDevices as Mock).mockResolvedValue({
      devices: [
        { deviceId: "aaa", label: "Built-in Mic" },
        { deviceId: "bbb", label: "USB Headset" },
      ],
      hasLabels: true,
    });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    expect(await screen.findByRole("option", { name: "USB Headset" })).toBeTruthy();
  });

  it("requests mic access when the picker is focused and labels aren't available yet", async () => {
    (listInputDevices as Mock)
      .mockResolvedValueOnce({ devices: [], hasLabels: false }) // mount: no labels yet
      .mockResolvedValue({ devices: [{ deviceId: "bbb", label: "USB Headset" }], hasLabels: true }); // after grant
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.focus(await screen.findByRole("combobox", { name: /^microphone$/i }));

    expect(await screen.findByRole("option", { name: "USB Headset" })).toBeTruthy();
    expect(unlockDeviceLabels).toHaveBeenCalled();
  });

  it("shows a no-microphone hint when focusing the picker but access can't be granted", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
    (unlockDeviceLabels as Mock).mockRejectedValue(
      Object.assign(new Error("x"), { name: "NotFoundError" }),
    );
    render(<Recorder onUploaded={() => {}} />);
    await openSource();

    fireEvent.focus(await screen.findByRole("combobox", { name: /^microphone$/i }));

    // The hint shows in the status bar (neutral tone), not inline under the top bar.
    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith(
        expect.stringMatching(/no microphone detected/i),
        "info",
        { sticky: true },
      ),
    );
    expect(screen.queryByText(/no microphone detected/i)).toBeNull();
  });

  it("no longer shows the bespoke 'allow microphone' link", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
    render(<Recorder onUploaded={() => {}} />);
    await openSource();
    await screen.findByRole("combobox", { name: /^microphone$/i });
    expect(screen.queryByRole("button", { name: /allow microphone/i })).toBeNull();
  });
});

describe("Recorder pause/resume", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  it("toggles Pause↔Resume while recording and shows a Paused indicator", async () => {
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));

    // Recording: a Pause button appears (and Stop). No Resume, no "Paused" yet.
    const pauseBtn = await screen.findByRole("button", { name: /^pause$/i });
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeTruthy();
    expect(screen.queryByText(/paused/i)).toBeNull();

    fireEvent.click(pauseBtn);

    // Paused: the button flips to Resume and a "Paused" indicator shows.
    expect(await screen.findByRole("button", { name: /^resume$/i })).toBeTruthy();
    expect(screen.getByText(/paused/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^pause$/i })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /^resume$/i }));

    // Resumed: back to Pause, no "Paused" indicator.
    expect(await screen.findByRole("button", { name: /^pause$/i })).toBeTruthy();
    expect(screen.queryByText(/paused/i)).toBeNull();
  });
});

describe("Recorder auto-stop", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  // The auto-stop control is now a clock icon button (accessible name "auto-stop") that opens the
  // Auto-stop popover; the choices are option rows inside it, not a native <select>.
  const chooseAutoStop = (name: RegExp) => {
    fireEvent.click(screen.getByRole("button", { name: /^auto-stop$/i }));
    fireEvent.click(screen.getByRole("button", { name }));
  };

  it("auto-stops the recording and uploads when the scheduled time is reached", async () => {
    vi.useFakeTimers();
    try {
      (getStream as Mock).mockResolvedValue(fakeSession);
      (api.upload as Mock).mockResolvedValue({ id: "r1" });
      render(<Recorder onUploaded={() => {}} />);
      // Flush the mount effects (device enumeration, pending-recording load) under fake timers.
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      // Choose "in 15 minutes" from the auto-stop popover, then start recording.
      chooseAutoStop(/stop in 15 minutes/i);
      fireEvent.click(screen.getByLabelText(/^record$/i));
      // Flush start()'s awaited getStream promise under fake timers.
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      // Nothing yet before the 15-minute mark.
      await act(async () => {
        vi.advanceTimersByTime(14 * 60_000);
      });
      expect(api.upload).not.toHaveBeenCalled();

      // Cross the mark: the schedule watcher sees shouldStop() and stops -> onstop -> upload().
      await act(async () => {
        await vi.advanceTimersByTimeAsync(60_500);
      });
      expect(api.upload).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("still auto-stops while paused (the schedule is a wall-clock time)", async () => {
    vi.useFakeTimers();
    try {
      (getStream as Mock).mockResolvedValue(fakeSession);
      (api.upload as Mock).mockResolvedValue({ id: "r1" });
      render(<Recorder onUploaded={() => {}} />);
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      chooseAutoStop(/stop in 15 minutes/i);
      fireEvent.click(screen.getByLabelText(/^record$/i));
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      // Pause 5 minutes in, then walk away.
      await act(async () => {
        vi.advanceTimersByTime(5 * 60_000);
      });
      fireEvent.click(screen.getByRole("button", { name: /^pause$/i }));

      // The elapsed ticker is frozen while paused, but the schedule watcher keeps running: at the
      // 15-minute wall-clock mark the recording still auto-stops and uploads.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(11 * 60_000);
      });
      expect(api.upload).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("announces the auto-stop with a toast naming the schedule rule", async () => {
    vi.useFakeTimers();
    try {
      (getStream as Mock).mockResolvedValue(fakeSession);
      (api.upload as Mock).mockResolvedValue({ id: "r1" });
      render(
        <ToastProvider>
          <Recorder onUploaded={() => {}} />
        </ToastProvider>,
      );
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      chooseAutoStop(/stop in 15 minutes/i);
      fireEvent.click(screen.getByLabelText(/^record$/i));
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      // Cross the 15-minute mark: the schedule watcher stops the recording on its own - nobody pressed
      // Stop - so it must say why.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15 * 60_000 + 500);
      });

      expect(screen.getByText("Recording stopped - your auto-stop time was reached.")).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("live notes", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
    (api.createNotes as Mock).mockResolvedValue([]);
  });

  it("shows the notes panel while recording and commits a stamped, mirrored line", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i); // auto-opened

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "budget concern" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(await screen.findByText("budget concern")).toBeTruthy();

    await waitFor(() =>
      expect(savePendingNotes).toHaveBeenCalledWith(
        expect.objectContaining({
          recordingId: null,
          lines: [expect.objectContaining({ text: "budget concern" })],
        }),
      ),
    );
  });

  // Edit and delete had no coverage at all before the useLiveNotes extraction. They pin the two
  // behaviours the extraction moves, so a mistake in the move shows up as a failure rather than as
  // silence. Both mutation-checked when written.
  it("edits a live line in place and re-mirrors the edited set", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "chase the invoice" } });
    fireEvent.keyDown(box, { key: "Enter" });
    await screen.findByText("chase the invoice");

    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    fireEvent.change(screen.getByLabelText(/edit note/i), { target: { value: "chase the invoice today" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    expect(await screen.findByText("chase the invoice today")).toBeTruthy();
    expect(screen.queryByText("chase the invoice")).toBeNull();
    await waitFor(() =>
      expect(savePendingNotes).toHaveBeenLastCalledWith(
        expect.objectContaining({
          recordingId: null,
          lines: [expect.objectContaining({ text: "chase the invoice today" })],
        }),
      ),
    );
  });

  it("deletes a live line and re-mirrors what is left", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "first line" } });
    fireEvent.keyDown(box, { key: "Enter" });
    await screen.findByText("first line");
    fireEvent.change(box, { target: { value: "second line" } });
    fireEvent.keyDown(box, { key: "Enter" });
    await screen.findByText("second line");

    fireEvent.click(screen.getAllByRole("button", { name: /delete note/i })[0]);

    await waitFor(() => expect(screen.queryByText("first line")).toBeNull());
    expect(screen.getByText("second line")).toBeTruthy();
    await waitFor(() =>
      expect(savePendingNotes).toHaveBeenLastCalledWith(
        expect.objectContaining({ lines: [expect.objectContaining({ text: "second line" })] }),
      ),
    );
  });

  it("attaches committed lines to the uploaded recording and clears the stash", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "follow up with legal" } });
    fireEvent.keyDown(box, { key: "Enter" });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() =>
      expect(api.createNotes).toHaveBeenCalledWith("rec-new", [
        expect.objectContaining({ text: "follow up with legal" }),
      ]),
    );
    await waitFor(() => expect(clearPendingNotes).toHaveBeenCalledWith("u1"));
  });

  it("keeps lines durable and offers a retry when the attach fails", async () => {
    (api.createNotes as Mock).mockRejectedValueOnce(new Error("boom"));
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "x" } });
    fireEvent.keyDown(box, { key: "Enter" });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    expect(await screen.findByText(/could not be attached/i)).toBeTruthy();
    await waitFor(() =>
      expect(savePendingNotes).toHaveBeenCalledWith(expect.objectContaining({ recordingId: "rec-new" })),
    );

    // Retry succeeds and the banner clears.
    fireEvent.click(screen.getByRole("button", { name: /attach notes/i }));
    await waitFor(() => expect(api.createNotes).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText(/could not be attached/i)).toBeNull());
  });

  it("does not re-offer the already-uploaded audio when a storage hiccup breaks note-attach recovery", async () => {
    // A pathological double failure: the notes attach API call fails, AND the durable-stash write inside
    // attachNotes' own catch block also throws (e.g. keyedStash's openDb() escaping - see keyedStash.test.ts).
    // The audio itself uploaded fine; a storage hiccup two steps downstream of that must never be mistaken
    // for the upload having failed (which would offer the already-uploaded recording back for a duplicate
    // upload).
    (api.createNotes as Mock).mockRejectedValueOnce(new Error("boom"));
    // The first call is mirrorLines' fire-and-forget mirror-to-stash on adding the note (unrelated to this
    // scenario); the second is the one inside attachNotes' own catch block, which is what must not escape.
    (savePendingNotes as Mock)
      .mockRejectedValueOnce(new Error("storage hiccup (mirror)"))
      .mockRejectedValueOnce(new Error("storage hiccup (attach catch)"));
    const onUploaded = vi.fn();
    render(<Recorder onUploaded={onUploaded} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "x" } });
    fireEvent.keyDown(box, { key: "Enter" });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    // The audio upload must still be treated as a success...
    await waitFor(() => expect(onUploaded).toHaveBeenCalled());
    // ...and the recording must never be re-offered as an unsaved/failed upload.
    expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull();
    expect(screen.queryByTestId("recorder-popover")).toBeNull();
  });

  it("closing the panel persists the preference; toggle reopens it", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);

    fireEvent.click(screen.getByRole("button", { name: /close notes/i }));
    expect(screen.queryByText(/notes while recording/i)).toBeNull();
    expect(localStorage.getItem("diariz.recorder.notesOpen")).toBe("false");

    fireEvent.click(screen.getByRole("button", { name: /^notes$/i }));
    expect(await screen.findByText(/notes while recording/i)).toBeTruthy();
  });
});

describe("live screenshots", () => {
  // A fake Electron shell exposing the screenshot bridge (see lib/trayScreenshots.ts). The real bridge
  // module is used unmocked - only its `window.diariz` dependency is faked, exactly like a real desktop
  // build would supply it. `emit` lets a test simulate the shell delivering a capture.
  let emit: ((payload: unknown) => void) | null = null;
  function installShell() {
    emit = null;
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      onScreenshotCaptured: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {
          emit = null;
        };
      },
    };
  }
  const capture = (overrides: Partial<{ width: number; height: number }> = {}) =>
    emit!({
      full: new Uint8Array([1, 2, 3]),
      thumb: new Uint8Array([4]),
      width: 800,
      height: 600,
      ...overrides,
    });

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
    (api.createScreenshot as Mock).mockResolvedValue({});
  });

  afterEach(() => {
    delete (window as unknown as { diariz?: unknown }).diariz;
  });

  it("does nothing in a plain browser (no window.diariz)", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    // No shell was installed, so there is nothing to emit and nothing should have been stashed.
    expect(addPendingScreenshot).not.toHaveBeenCalled();
  });

  it("ignores a capture that arrives while not recording", async () => {
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /^record$/i }); // mounted, not recording

    capture();

    expect(addPendingScreenshot).not.toHaveBeenCalled();
  });

  it("stamps a capture with the recorded clock and stashes exactly that one capture", async () => {
    // The whole point of the per-item stash: adding a capture writes only that capture, not the whole
    // growing set - so the call carries a single shot object, not an array.
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    capture({ width: 640, height: 480 });

    await waitFor(() =>
      expect(addPendingScreenshot).toHaveBeenCalledWith(
        "u1",
        expect.objectContaining({ width: 640, height: 480, capturedAtMs: expect.any(Number), id: expect.any(String) }),
      ),
    );
  });

  it("stashes a second capture without rewriting the first (one write per capture, not the whole set)", async () => {
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    capture({ width: 111, height: 111 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(1));
    capture({ width: 222, height: 222 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(2));

    // Each call carries exactly one capture - never a growing array of everything captured so far.
    for (const call of (addPendingScreenshot as Mock).mock.calls) {
      const arg = call[1] as PendingShot;
      expect(Array.isArray((arg as unknown as { shots?: unknown }).shots)).toBe(false);
    }
  });

  it("attaches stashed captures to the uploaded recording and clears the stash", async () => {
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    capture();
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() =>
      expect(api.createScreenshot).toHaveBeenCalledWith(
        "rec-new",
        expect.objectContaining({ width: 800, height: 600 }),
      ),
    );
    // The uploaded capture is removed as its own record once it lands.
    const shotArg = (addPendingScreenshot as Mock).mock.calls[0][1] as PendingShot;
    await waitFor(() => expect(removePendingScreenshot).toHaveBeenCalledWith("u1", shotArg.id));
    await waitFor(() => expect(clearPendingScreenshots).toHaveBeenCalledWith("u1"));
  });

  it("removes only the un-uploaded remainder's un-attached state when a later capture in the batch fails", async () => {
    installShell();
    (api.createScreenshot as Mock).mockResolvedValueOnce({}).mockRejectedValueOnce(new Error("boom"));
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    capture({ width: 111, height: 111 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(1));
    capture({ width: 222, height: 222 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(2));
    const firstShot = (addPendingScreenshot as Mock).mock.calls[0][1] as PendingShot;
    const secondShot = (addPendingScreenshot as Mock).mock.calls[1][1] as PendingShot;

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.createScreenshot).toHaveBeenCalledTimes(2));
    // Only the first (successfully-uploaded) capture is removed from the durable store - the second
    // (un-uploaded) one is left in place, so a retry can't re-post what already reached the server and
    // doesn't need to rediscover what's left: it was never removed.
    await waitFor(() => expect(removePendingScreenshot).toHaveBeenCalledWith("u1", firstShot.id));
    expect(removePendingScreenshot).not.toHaveBeenCalledWith("u1", secondShot.id);
    // The recordingId meta is set so a future retry/recovery knows where the remainder belongs.
    await waitFor(() => expect(setPendingScreenshotsRecordingId).toHaveBeenCalledWith("u1", "rec-new"));
  });

  it("keeps captures durable and offers a retry when the attach fails, without failing the upload", async () => {
    installShell();
    (api.createScreenshot as Mock).mockRejectedValueOnce(new Error("boom"));
    const onUploaded = vi.fn();
    render(<Recorder onUploaded={onUploaded} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    capture();
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    // The audio upload still succeeds even though the screenshot attach threw.
    await waitFor(() => expect(onUploaded).toHaveBeenCalled());
    expect(await screen.findByText(/screenshots were saved but could not be attached/i)).toBeTruthy();
    await waitFor(() => expect(setPendingScreenshotsRecordingId).toHaveBeenCalledWith("u1", "rec-new"));

    // Retry succeeds and the banner clears.
    fireEvent.click(screen.getByRole("button", { name: /attach screenshots/i }));
    await waitFor(() => expect(api.createScreenshot).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText(/screenshots were saved but could not be attached/i)).toBeNull());
  });

  it("keeps a capture taken while paused, stamped at the paused clock position", async () => {
    // A paused recording is still live: a capture taken between pause() and resume() must be kept (not
    // dropped) and stamped with the recorded clock frozen at the moment of pause - wall-clock time that
    // passes while paused must not leak into capturedAtMs.
    vi.useFakeTimers();
    try {
      installShell();
      render(<Recorder onUploaded={() => {}} />);
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      fireEvent.click(screen.getByLabelText(/^record$/i));
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });

      // 5 seconds into the recording, pause.
      await act(async () => {
        vi.advanceTimersByTime(5_000);
      });
      fireEvent.click(screen.getByRole("button", { name: /^pause$/i }));

      // Capture #1 right at pause - kept (not dropped) and stamped at the paused clock position.
      await act(async () => {
        capture({ width: 111, height: 111 });
      });
      const firstShot = (addPendingScreenshot as Mock).mock.calls.at(-1)![1] as PendingShot;
      const pausedCapturedAtMs = firstShot.capturedAtMs;

      // Time keeps moving in the real world while still paused - the recorded clock must not.
      await act(async () => {
        vi.advanceTimersByTime(10_000);
      });
      await act(async () => {
        capture({ width: 222, height: 222 });
      });

      expect(addPendingScreenshot).toHaveBeenCalledTimes(2); // both captures kept, none dropped
      const secondShot = (addPendingScreenshot as Mock).mock.calls.at(-1)![1] as PendingShot;
      expect(secondShot.capturedAtMs).toBe(pausedCapturedAtMs); // clock frozen, not advanced by the 10s paused wait
    } finally {
      vi.useRealTimers();
    }
  });

  it("clears a stale, never-attached stash when a new recording starts", async () => {
    installShell();
    (api.upload as Mock).mockRejectedValueOnce(new Error("network down"));
    render(<Recorder onUploaded={() => {}} />);

    // First recording: capture one shot, then the audio upload itself fails (never reaches attach).
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    capture();
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalled());
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));

    (clearPendingScreenshots as Mock).mockClear();

    // Starting a fresh recording must not carry the orphaned capture over into the new take.
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));

    await waitFor(() => expect(clearPendingScreenshots).toHaveBeenCalledWith("u1"));
  });

  it("deletes exactly the right capture from the durable store when removed from the live strip", async () => {
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    capture({ width: 111, height: 111 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(1));
    capture({ width: 222, height: 222 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(2));
    const firstShot = (addPendingScreenshot as Mock).mock.calls[0][1] as PendingShot;
    const secondShot = (addPendingScreenshot as Mock).mock.calls[1][1] as PendingShot;

    // The notes/screenshots popover auto-opens on Record (per the remembered preference, default open) -
    // delete the first (index 0) capture from the live strip.
    fireEvent.click((await screen.findAllByRole("button", { name: /delete/i }))[0]);

    await waitFor(() => expect(removePendingScreenshot).toHaveBeenCalledWith("u1", firstShot.id));
    expect(removePendingScreenshot).not.toHaveBeenCalledWith("u1", secondShot.id);
  });

  it("stops growing the capture stash past a sane cap and tells the user the capture was not kept", async () => {
    // Guards against unbounded growth (a runaway held hotkey, or a marathon meeting): past the cap, the
    // stash simply stops accepting new captures rather than silently degrading (see MAX_LIVE_SCREENSHOTS'
    // comment in Recorder.tsx for the current, memory-based justification).
    installShell();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    // addLiveShot updates the ref/state and fires addPendingScreenshot synchronously (fire-and-forget),
    // so all 200 captures can be fired in one act() and asserted once - no need to await each individually.
    act(() => {
      for (let i = 0; i < MAX_LIVE_SCREENSHOTS; i++) {
        capture({ width: i, height: i });
      }
    });
    expect(addPendingScreenshot).toHaveBeenCalledTimes(MAX_LIVE_SCREENSHOTS);

    capture({ width: 999, height: 999 }); // the 201st capture - must be dropped, not stashed
    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith(expect.stringMatching(/limit/i), expect.any(String), {
        sticky: true,
      }),
    );
    expect(addPendingScreenshot).toHaveBeenCalledTimes(MAX_LIVE_SCREENSHOTS); // no 201st write
  });
});

describe("in-app capture button", () => {
  function installShellWithCapture() {
    const captureScreenshot = vi.fn();
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      captureScreenshot,
      onScreenshotCaptured: () => () => {},
    };
    return captureScreenshot;
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

  it("is absent in a plain browser (no shell), even while recording", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    expect(screen.queryByRole("button", { name: /capture screenshot/i })).toBeNull();
  });

  it("is absent before recording starts, even when the shell can capture", async () => {
    installShellWithCapture();
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /^record$/i });

    expect(screen.queryByRole("button", { name: /capture screenshot/i })).toBeNull();
  });

  it("appears while recording when the shell can capture", async () => {
    installShellWithCapture();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));

    expect(await screen.findByRole("button", { name: /capture screenshot/i })).toBeTruthy();
  });

  it("disappears again once the recording stops", async () => {
    installShellWithCapture();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /capture screenshot/i });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(screen.queryByRole("button", { name: /capture screenshot/i })).toBeNull());
  });

  it("requests a capture from the shell when clicked", async () => {
    const captureScreenshot = installShellWithCapture();
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));

    fireEvent.click(await screen.findByRole("button", { name: /capture screenshot/i }));

    expect(captureScreenshot).toHaveBeenCalledTimes(1);
  });
});

describe("screenshot attach progress feedback", () => {
  // A fake Electron shell, same shape as the "live screenshots" describe above.
  let emit: ((payload: unknown) => void) | null = null;
  function installShell() {
    emit = null;
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      onScreenshotCaptured: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {
          emit = null;
        };
      },
    };
  }
  const capture = (overrides: Partial<{ width: number; height: number }> = {}) =>
    emit!({ full: new Uint8Array([1]), thumb: new Uint8Array([2]), width: 800, height: 600, ...overrides });

  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((res) => {
      resolve = res;
    });
    return { promise, resolve };
  }

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
  });

  afterEach(() => {
    delete (window as unknown as { diariz?: unknown }).diariz;
  });

  // The idle record pill doubles as the busy indicator (title/aria-label = "Uploading…") once Stop is
  // pressed - the only user-visible status surface while upload() runs. This is the "existing status-text
  // mechanism" FIX 3 works within, rather than adding a new one.
  const heroTitle = () => screen.getByRole("button", { name: /record|uploading|screenshot/i }).getAttribute("title");

  it("distinguishes uploading captures from the generic 'Uploading' text, with progress counts", async () => {
    installShell();
    const first = deferred<object>();
    const second = deferred<object>();
    (api.createScreenshot as Mock).mockImplementationOnce(() => first.promise).mockImplementationOnce(() => second.promise);

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    capture({ width: 1, height: 1 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(1));
    capture({ width: 2, height: 2 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(2));

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(api.createScreenshot).toHaveBeenCalledTimes(1));

    // The audio has already uploaded (createScreenshot only runs after it) - the busy text must say so
    // and show progress, not just repeat the generic "Uploading…" of the audio phase. The first capture
    // is in flight but not yet resolved, so progress reads 0 of 2 done.
    await waitFor(() => expect(heroTitle()).not.toBe("Uploading…"));
    await waitFor(() => expect(heroTitle()).toMatch(/0\s*\/\s*2/));

    first.resolve({});
    await waitFor(() => expect(api.createScreenshot).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(heroTitle()).toMatch(/1\s*\/\s*2/));

    second.resolve({});
    await waitFor(() => expect(screen.getByRole("button", { name: /record/i })).toBeTruthy());
  });

  it("does not show attach progress for an ordinary audio-only upload (no captures taken)", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect(api.createScreenshot).not.toHaveBeenCalled();
  });
});

// ---- Upload progress in the status bar ----

/// Pressing Stop disables the Record button and then does nothing visible while the audio uploads - 5-10
/// seconds on a long recording, which reads as a freeze. The status bar carries an "Uploading... Ns"
/// count-up for the whole busy window instead. See lib/elapsedSeconds.ts for the counter itself.
describe("upload progress in the status bar", () => {
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((res) => {
      resolve = res;
    });
    return { promise, resolve };
  }

  // A fake Electron shell, same shape as the "screenshot attach progress feedback" describe above.
  let emit: ((payload: unknown) => void) | null = null;
  function installShell() {
    emit = null;
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      onScreenshotCaptured: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {
          emit = null;
        };
      },
    };
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

  async function recordThenStop() {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
  }

  it("announces the upload the moment it starts, and clears once it finishes", async () => {
    const upload = deferred<{ id: string }>();
    (api.upload as Mock).mockReturnValue(upload.promise);

    await recordThenStop();

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith("Uploading… 0s", "progress", { sticky: true }),
    );

    setStatus.mockClear();
    upload.resolve({ id: "r1" });
    await waitFor(() => expect(setStatus).toHaveBeenCalledWith(null));
  });

  it("counts up while the upload is still in flight", async () => {
    vi.useFakeTimers();
    try {
      const upload = deferred<{ id: string }>();
      (api.upload as Mock).mockReturnValue(upload.promise);

      render(<Recorder onUploaded={() => {}} />);
      // Flush the mount effects (device enumeration, pending-recording load) under fake timers.
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });
      fireEvent.click(screen.getByLabelText(/^record$/i));
      // Flush start()'s awaited getStream promise under fake timers.
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });
      fireEvent.click(screen.getByLabelText(/^stop$/i));
      await act(async () => {
        await vi.runOnlyPendingTimersAsync();
      });
      expect(api.upload).toHaveBeenCalledTimes(1);

      // Asserted as a delta rather than an absolute count: flushing the mount and start effects above
      // advances the fake clock by an amount that is fake-timer bookkeeping, not behaviour. The delta is
      // what matters, and is 0 for a counter that renders once and then sits still.
      const secondsIn = (call: unknown[] | undefined) => {
        const m = /^Uploading… (\d+)s$/.exec(String(call?.[0]));
        expect(m, `last status was ${String(call?.[0])}`).not.toBeNull();
        expect(call?.[1]).toBe("progress");
        expect(call?.[2]).toEqual({ sticky: true });
        return Number(m![1]);
      };

      const before = secondsIn(setStatus.mock.calls.at(-1));
      await act(async () => {
        vi.advanceTimersByTime(3000);
      });
      expect(secondsIn(setStatus.mock.calls.at(-1)) - before).toBeGreaterThanOrEqual(3);
    } finally {
      vi.useRealTimers();
    }
  });

  // The audio is already up by the time captures start posting, and each is its own request - so the bar
  // keeps the attach's own "n/total" wording rather than falling back to a generic "Uploading", and keeps
  // counting so a long attach is still visibly alive.
  it("keeps the screenshot-attach wording, with the count still running", async () => {
    installShell();
    const shot = deferred<object>();
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
    (api.createScreenshot as Mock).mockReturnValue(shot.promise);

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    emit!({ full: new Uint8Array([1]), thumb: new Uint8Array([2]), width: 800, height: 600 });
    await waitFor(() => expect(addPendingScreenshot).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(api.createScreenshot).toHaveBeenCalledTimes(1));

    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith(
        expect.stringMatching(/^Recording saved, uploading screenshots 0\/1… \d+s$/),
        "progress",
        { sticky: true },
      ),
    );

    setStatus.mockClear();
    shot.resolve({});
    await waitFor(() => expect(setStatus).toHaveBeenCalledWith(null));
  });

  // A record-start notice (here: system audio wasn't shared, so we fell back to mic-only) is stale by the
  // time you press Stop, and must not sit in the bar in place of live upload progress. The reverse holds
  // for `error`, covered by the test below.
  it("replaces a stale record-start notice while the upload runs", async () => {
    (getCombinedStream as Mock).mockRejectedValue(Object.assign(new Error("x"), { name: "NotAllowedError" }));
    const upload = deferred<{ id: string }>();
    (api.upload as Mock).mockReturnValue(upload.promise);

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /audio source/i }));
    fireEvent.click(await screen.findByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    await waitFor(() =>
      expect(setStatus).toHaveBeenCalledWith(expect.stringMatching(/microphone only/i), "progress", {
        sticky: true,
      }),
    );

    setStatus.mockClear();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    await waitFor(() =>
      expect(setStatus).toHaveBeenLastCalledWith(
        expect.stringMatching(/^Uploading… \d+s$/),
        "progress",
        { sticky: true },
      ),
    );
  });

  // A failed upload still reports the failure, and the count-up is gone by then (setBusy(false) is batched
  // with setError, so no render shows both). The `error ?? uploadText` ordering above it is therefore
  // defensive - deliberately not asserted here, because nothing today can reach it.
  it("gives way to the error message when the upload fails", async () => {
    (api.upload as Mock).mockRejectedValue(new Error("nope"));

    await recordThenStop();

    await waitFor(() => expect(setStatus).toHaveBeenCalledWith("Upload failed.", "error", { sticky: true }));
  });
});

// ---- Recording started from a calendar event ----

describe("recording started from a calendar event", () => {
  const EVENT = {
    id: "evt-1",
    summary: "Quarterly review with Acme",
    endsAt: "2026-08-09T11:00:00.000Z",
    calendarId: "work@example.com",
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    calendarSettings.enabled = false;
    calendarSettings.afterMinutes = 3;
    calendarSettings.silenceSeconds = 30;
    silenceWatcher.onSilent = null;
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
    (api.renameRecording as Mock).mockResolvedValue(undefined);
    (api.putCalendarLink as Mock).mockResolvedValue({});
  });

  /// Drive the same channel the Join button uses.
  async function joinAndRecord(event: typeof EVENT | undefined = EVENT) {
    requestRecording(event ? { calendarEvent: event } : {});
    await screen.findByRole("button", { name: /^stop$/i });
  }

  it("names the recording after the invite, and pins it so the summariser cannot rename it", async () => {
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    // The title carries the invite subject instead of "Microphone <timestamp>"...
    expect((api.upload as Mock).mock.calls[0][1]).toBe("Quarterly review with Acme");
    // ...and the NAME is set too: the summariser auto-names a recording whose name is blank, so leaving it
    // unset would let the model rename the meeting away from what the invite called it.
    await waitFor(() => expect(api.renameRecording).toHaveBeenCalledWith("rec-new", "Quarterly review with Acme"));
  });

  it("links the recording to the event it was started from, as a deliberate choice", async () => {
    // The event id is known at record time, so the link is certain rather than inferred from time overlap.
    // Marked manual so the auto-matcher can never replace it with an adjacent meeting.
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() =>
      expect(api.putCalendarLink).toHaveBeenCalledWith("rec-new", "evt-1", true, "work@example.com"),
    );
  });

  it("still saves the recording when linking fails", async () => {
    // No calendar connected, the event since deleted, a flaky call - none of that may look like a lost
    // recording. The audio is already uploaded by this point.
    (api.putCalendarLink as Mock).mockRejectedValue(new Error("no calendar connected"));
    const onUploaded = vi.fn();
    render(<Recorder onUploaded={onUploaded} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(onUploaded).toHaveBeenCalled());
    expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull();
  });

  it("does not link an ordinary Record-button take", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect(api.putCalendarLink).not.toHaveBeenCalled();
  });

  it("keeps the generated title for an ordinary Record-button take", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][1]).not.toBe("Quarterly review with Acme");
    expect(api.renameRecording).not.toHaveBeenCalled();
  });

  it("does not carry the last meeting's name onto the next manual recording", async () => {
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(2));
    expect((api.upload as Mock).mock.calls[1][1]).not.toBe("Quarterly review with Acme");
  });

  it("arms no silence watcher while auto-stop is off", async () => {
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();

    expect(startSilenceWatcher).not.toHaveBeenCalled();
  });

  it("stops the recording after the configured run of silence", async () => {
    calendarSettings.enabled = true;
    calendarSettings.silenceSeconds = 45;
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();

    // Armed with the user's threshold, in milliseconds.
    expect(startSilenceWatcher).toHaveBeenCalledWith(fakeStream, 45_000, expect.any(Function));

    // The meeting breaks up: the watcher fires, and the recording ends and uploads like any other.
    act(() => silenceWatcher.onSilent!());
    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect(await screen.findByRole("button", { name: /record/i })).toBeTruthy();
  });

  it("announces the silence auto-stop with a toast naming the rule", async () => {
    calendarSettings.enabled = true;
    calendarSettings.silenceSeconds = 45;
    render(
      <ToastProvider>
        <Recorder onUploaded={() => {}} />
      </ToastProvider>,
    );
    await screen.findByRole("button", { name: /record/i });

    await joinAndRecord();

    // Nobody pressed Stop - the meeting just went quiet - so the toast must say so.
    act(() => silenceWatcher.onSilent!());
    await waitFor(() => expect(api.upload).toHaveBeenCalled());

    expect(await screen.findByText("Recording stopped - the meeting went quiet.")).toBeTruthy();
  });

  it("suspends silence counting while paused, and resumes with it", async () => {
    // Pausing disables the capture track, so the analyser reads pure silence - counting through a break
    // would end the recording the user deliberately paused.
    calendarSettings.enabled = true;
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });
    await joinAndRecord();

    fireEvent.click(screen.getByRole("button", { name: /pause/i }));
    expect(silenceWatcher.setPaused).toHaveBeenCalledWith(true);

    fireEvent.click(screen.getByRole("button", { name: /resume/i }));
    expect(silenceWatcher.setPaused).toHaveBeenCalledWith(false);
  });

  it("tears the silence watcher down when the recording stops", async () => {
    calendarSettings.enabled = true;
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });
    await joinAndRecord();

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(silenceWatcher.stop).toHaveBeenCalled());
  });

  it("replaces a running recording: the first take is uploaded on its own before the second begins", async () => {
    // Joining a second meeting while the first is still recording. The first must complete its own pipeline
    // (upload + transcription), not be discarded, and must not be filed under the second take's context.
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    await joinAndRecord();

    // Exactly one upload so far - the first take - and the recorder is running again for the second.
    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));
    expect((api.upload as Mock).mock.calls[0][1]).not.toBe("Quarterly review with Acme");
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeTruthy();

    // The second take is the meeting, and uploads separately.
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(2));
    expect((api.upload as Mock).mock.calls[1][1]).toBe("Quarterly review with Acme");
  });

  it("does not start the replacement until the outgoing upload has settled", async () => {
    // upload() reads the room/folder refs after its first await, so a replacement that started underneath it
    // would file the finished recording into the NEW take's folder.
    let release!: (v: unknown) => void;
    (api.upload as Mock).mockImplementationOnce(
      () => new Promise((resolve) => { release = resolve; }),
    );
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    requestRecording({ calendarEvent: EVENT });
    await waitFor(() => expect(api.upload).toHaveBeenCalledTimes(1));

    // The first upload is still in flight, so the second recording has NOT begun.
    expect(getStream).toHaveBeenCalledTimes(1);

    release({ id: "rec-first" });
    await waitFor(() => expect(getStream).toHaveBeenCalledTimes(2));
  });
});

// ---- Extending a meeting that overruns its invite ----
//
// The whole point of the feature: at the calendar's stop time, a meeting people are still audibly in must be
// *asked* about rather than cut off. Unanswered, it keeps recording - the silence rule is the floor.

describe("extending a meeting that overruns", () => {
  const EVENT = {
    id: "evt-1",
    summary: "Quarterly review with Acme",
    endsAt: "2026-08-09T11:00:00.000Z", // already past, so the stop lands afterMinutes from record-start
    calendarId: "work@example.com",
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    calendarSettings.enabled = true;
    calendarSettings.afterMinutes = 3;
    calendarSettings.silenceSeconds = 30;
    silenceState = { heardSound: true, silentMs: 0 }; // someone is talking right now
    silenceWatcher.onSilent = null;
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "rec-new" });
    (api.renameRecording as Mock).mockResolvedValue(undefined);
    (api.putCalendarLink as Mock).mockResolvedValue({});
  });

  afterEach(() => {
    vi.useRealTimers();
    silenceState = { heardSound: true, silentMs: 0 };
  });

  /// Everything here is wall-clock driven, so the whole block runs on fake timers. Mount, flush the mount
  /// effects, then drive the same channel the calendar's Join button uses. The clock is pinned to a whole
  /// minute so the "stops at HH:MM" assertions below cannot straddle a minute boundary.
  async function joinUnderFakeTimers(withToast = false) {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse("2026-08-10T09:00:00.000Z"));
    const ui = <Recorder onUploaded={() => {}} />;
    render(withToast ? <ToastProvider>{ui}</ToastProvider> : ui);
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
    requestRecording({ calendarEvent: EVENT });
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeTruthy();
  }

  const tick = async (ms: number) => {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  };

  it("asks instead of ending a meeting people are still in", async () => {
    await joinUnderFakeTimers();

    await tick(3 * 60_000 + 1_500);

    const prompt = screen.getByTestId("extend-prompt");
    expect(prompt.textContent).toContain("This meeting was due to end, but people are still talking.");
    // Still recording, nothing uploaded - the ending has not been lost.
    expect(api.upload).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeTruthy();
    // The silence rule is the floor here, so the prompt carries no countdown.
    expect(prompt.textContent).not.toMatch(/Ending in/);
  });

  it("just ends the take when the room has already gone quiet - there is nobody to ask", async () => {
    silenceState = { heardSound: true, silentMs: 30_000 };
    await joinUnderFakeTimers();

    await tick(3 * 60_000 + 1_500);

    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).toHaveBeenCalledTimes(1);
  });

  it("does not ask when nothing was ever heard - joined early and the meeting never happened", async () => {
    silenceState = { heardSound: false, silentMs: 0 };
    await joinUnderFakeTimers();

    await tick(3 * 60_000 + 1_500);

    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).toHaveBeenCalledTimes(1);
  });

  it("keeps recording for as long as the prompt goes unanswered", async () => {
    await joinUnderFakeTimers();
    await tick(3 * 60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();

    // Half an hour with nobody at the keyboard. No timeout, no default stop: the silence watcher is the
    // floor, and it has not fired.
    await tick(30 * 60_000);

    expect(api.upload).not.toHaveBeenCalled();
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
  });

  it("never negotiates the user's own auto-stop", async () => {
    // The user asked for a hard stop at a time they chose. Only the calendar-derived stop asks - which is
    // why the two targets are separate refs. Merged, this would show the prompt instead of stopping.
    calendarSettings.afterMinutes = 60; // calendar target well beyond the user's own
    vi.useFakeTimers();
    render(<Recorder onUploaded={() => {}} />);
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
    fireEvent.click(screen.getByRole("button", { name: /^auto-stop$/i }));
    fireEvent.click(screen.getByRole("button", { name: /stop in 15 minutes/i }));
    requestRecording({ calendarEvent: EVENT });
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });

    await tick(15 * 60_000 + 1_500);

    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).toHaveBeenCalledTimes(1);
  });

  it("Extend this meeting pushes the stop out by the overrun allowance instead of ending", async () => {
    await joinUnderFakeTimers();
    await tick(3 * 60_000 + 1_500);

    fireEvent.click(screen.getByRole("button", { name: /extend this meeting/i }));
    expect(screen.queryByTestId("extend-prompt")).toBeNull();

    // Not asked again before the fresh three minutes are up...
    await tick(2 * 60_000);
    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).not.toHaveBeenCalled();

    // ...and asked again once they are, because people are still talking. Never silently ended.
    await tick(60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
    expect(api.upload).not.toHaveBeenCalled();
  });

  it("Stop now ends the take, and raises no toast - the user just said so", async () => {
    // Regression guard for the same trap RecordHero's Stop hit: `stop` takes an optional StopReason, so
    // wiring it bare as onClick hands React's SyntheticEvent in as the reason and raises a bogus toast.
    await joinUnderFakeTimers(true);
    await tick(3 * 60_000 + 1_500);

    fireEvent.click(screen.getByRole("button", { name: /stop now/i }));
    await tick(1_000);

    expect(api.upload).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(document.querySelector('[role="status"]')?.children.length).toBe(0);
  });

  it("counts down and ends by itself only for a user who has turned the silence rule off", async () => {
    // The one case with no floor under an unanswered prompt: without the silence watcher to end the take
    // when the room empties, recording until the browser closes would be worse than a visible deadline.
    calendarSettings.silenceSeconds = 0;
    await joinUnderFakeTimers(true);
    await tick(3 * 60_000 + 1_500);

    expect(screen.getByTestId("extend-prompt").textContent).toMatch(/Ending in \d+s/);

    // Still recording two minutes in - the countdown is a last resort, not a short leash.
    await tick(2 * 60_000);
    expect(api.upload).not.toHaveBeenCalled();
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();

    // Then it ends by itself, and says why. (This last step deliberately only just clears the deadline: the
    // toast auto-dismisses after four seconds, so advancing minutes past it would sweep it away unseen.)
    await tick(61_000);
    expect(api.upload).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Recording stopped - the meeting has ended.")).toBeTruthy();
  });

  it("raises an OS notification, because the user is in Teams rather than looking at Diariz", async () => {
    const raised = vi.fn();
    class FakeNotification {
      static permission = "granted";
      static requestPermission = vi.fn();
      constructor(title: string, opts: unknown) {
        raised(title, opts);
      }
    }
    (globalThis as unknown as { Notification?: unknown }).Notification = FakeNotification;
    try {
      await joinUnderFakeTimers();
      await tick(3 * 60_000 + 1_500);

      expect(raised).toHaveBeenCalledWith("Still recording?", {
        body: "This meeting was due to end, but people are still talking.",
      });
    } finally {
      delete (globalThis as unknown as { Notification?: unknown }).Notification;
    }
  });

  it("clears the prompt when the recording ends, so the next take never inherits it", async () => {
    await joinUnderFakeTimers();
    await tick(3 * 60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await tick(1_000);

    expect(screen.queryByTestId("extend-prompt")).toBeNull();
  });

  it("ends a paused recording on time rather than asking - a paused room has nobody in it", async () => {
    // Pausing freezes the silence watcher (it would otherwise read the disabled track as silence), so its
    // last reading is stale speech. Asking on that would show a prompt with no floor under it - the silence
    // rule is paused too - and the take would never end. Before this feature it stopped and uploaded on
    // time, and it still must.
    await joinUnderFakeTimers();
    await tick(30_000);
    fireEvent.click(screen.getByRole("button", { name: /^pause$/i }));

    await tick(3 * 60_000 + 1_500);

    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).toHaveBeenCalledTimes(1);
  });

  it("uses the user's own silence setting as the window, not a fixed one", async () => {
    // 20 seconds of quiet with a 30-second silence rule: that rule still considers the meeting running, so
    // the take must not be ended silently behind its back. A fixed 10-second window would stop here.
    calendarSettings.silenceSeconds = 30;
    silenceState = { heardSound: true, silentMs: 20_000 };
    await joinUnderFakeTimers();

    await tick(3 * 60_000 + 1_500);

    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
    expect(api.upload).not.toHaveBeenCalled();
  });

  it("falls back to the built-in window when the silence setting is not a usable number", async () => {
    // A malformed settings payload must not make the comparison NaN, which would refuse to ask at all.
    calendarSettings.silenceSeconds = Number.NaN;
    silenceState = { heardSound: true, silentMs: 2_000 };
    await joinUnderFakeTimers();

    await tick(3 * 60_000 + 1_500);

    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
  });

  it("backs off: the second extension lasts twice as long as the first", async () => {
    await joinUnderFakeTimers();
    await tick(3 * 60_000 + 1_500);
    fireEvent.click(screen.getByRole("button", { name: /extend this meeting/i }));

    // First extension: the user's own three minutes.
    await tick(3 * 60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /extend this meeting/i }));

    // Second: six, not three - so a half-hour overrun is a couple of prompts, not ten.
    await tick(3 * 60_000 + 1_500);
    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    await tick(3 * 60_000);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
    expect(api.upload).not.toHaveBeenCalled();
  });

  it("starts each take's back-off from scratch", async () => {
    // The count is per take. Left running, a second meeting would inherit the first's back-off and go six
    // minutes before its FIRST extension expired - so this has to extend on the second take to see it.
    await joinUnderFakeTimers();
    await tick(3 * 60_000 + 1_500);
    fireEvent.click(screen.getByRole("button", { name: /extend this meeting/i }));
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await tick(1_000);

    requestRecording({ calendarEvent: EVENT });
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
    await tick(3 * 60_000 + 1_500);
    fireEvent.click(screen.getByRole("button", { name: /extend this meeting/i }));

    // Three minutes, the user's own allowance - not the six a leaked count would give.
    await tick(3 * 60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();
  });

  it("does not carry a calendar stop onto the next plain Record-button take", async () => {
    await joinUnderFakeTimers();
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await tick(1_000);

    fireEvent.click(screen.getByLabelText(/^record$/i));
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });

    // An ordinary take has no meeting to end, so nothing may stop it but the user.
    await tick(10 * 60_000);
    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(api.upload).toHaveBeenCalledTimes(1); // the calendar take only
  });

  it("lets the silence rule end an unanswered prompt - the floor that makes no countdown safe", async () => {
    await joinUnderFakeTimers(true);
    await tick(3 * 60_000 + 1_500);
    expect(screen.getByTestId("extend-prompt")).toBeTruthy();

    // Everyone leaves. The watcher fires on its own threshold and the take ends, saying why.
    await act(() => {
      silenceWatcher.onSilent!();
    });
    await tick(1_000);

    expect(api.upload).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("extend-prompt")).toBeNull();
    expect(screen.getByText("Recording stopped - the meeting went quiet.")).toBeTruthy();
  });

  it("keeps showing the calendar's stop time when the user changes their own auto-stop mid-meeting", async () => {
    // The display shows whichever target comes first. Overwriting it with the user's own choice alone would
    // claim the recording runs fifteen minutes when the meeting ends it in three.
    await joinUnderFakeTimers();
    const calendarStop = new Date(Date.now() + 3 * 60_000).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
    });

    fireEvent.click(screen.getByRole("button", { name: /^auto-stop$/i }));
    fireEvent.click(screen.getByRole("button", { name: /stop in 15 minutes/i }));

    expect(screen.getByText(`stops at ${calendarStop}`)).toBeTruthy();
  });
});

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

    fireEvent.click(await screen.findByRole("button", { name: /separate window/i }));

    expect(openNotesPopout).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByTestId("notes-popover")).toBeNull());
  });
});

// The narrow-window overflow menu. Below the cramped tier the secondary icon buttons are hidden by a
// container query and this menu stands in for them, so nothing the bar offers becomes unreachable however
// narrow the window gets. Every assertion here is scoped with `within(dialog)`: the inline buttons are
// hidden by CSS in a browser but jsdom loads no CSS, so an unscoped query for "Auto-stop" matches both the
// menu row and the inline button.
describe("Recorder overflow menu", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  afterEach(() => {
    delete (window as unknown as { diariz?: unknown }).diariz;
  });

  function installShellWithCapture() {
    const captureScreenshot = vi.fn();
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      captureScreenshot,
      onScreenshotCaptured: () => () => {},
    };
    return captureScreenshot;
  }

  const openMenu = async () => {
    fireEvent.click(await screen.findByRole("button", { name: /more controls/i }));
    return within(await screen.findByRole("dialog", { name: /more controls/i }));
  };

  it("offers auto-stop and upload while idle", async () => {
    render(<Recorder onUploaded={() => {}} />);
    const menu = await openMenu();

    expect(menu.getByRole("button", { name: "Auto-stop" })).toBeTruthy();
    expect(menu.getByRole("button", { name: "Upload" })).toBeTruthy();
    expect(menu.queryByRole("button", { name: "Notes" })).toBeNull();
  });

  // Upload is disabled for the whole of a recording, so a row for it could only ever be greyed. The menu
  // exists because room is short; a permanently dead row is the last thing it should spend a line on.
  it("swaps upload for notes while recording", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    const menu = await openMenu();

    expect(menu.getByRole("button", { name: "Notes" })).toBeTruthy();
    expect(menu.queryByRole("button", { name: "Upload" })).toBeNull();
  });

  it("offers the screenshot row only when the shell can capture, while recording", async () => {
    installShellWithCapture();
    render(<Recorder onUploaded={() => {}} />);

    expect((await openMenu()).queryByRole("button", { name: "Capture screenshot" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /more controls/i })); // close
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    expect((await openMenu()).getByRole("button", { name: "Capture screenshot" })).toBeTruthy();
  });

  // The load-bearing claim of this design: a menu row does not nest a popover inside the menu. HubPopover
  // panels are absolute children of their own `relative` wrapper in the bar, so they drop from the bar
  // wherever they were opened from - and the hub's one-open-at-a-time state closes the menu on the way.
  it("opens the auto-stop popover from the menu, closing the menu rather than nesting", async () => {
    render(<Recorder onUploaded={() => {}} />);
    const menu = await openMenu();

    fireEvent.click(menu.getByRole("button", { name: "Auto-stop" }));

    expect(await screen.findByRole("button", { name: /in 15 minutes/i })).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /more controls/i })).toBeNull();
  });

  it("opens the notes popover from the menu, closing the menu rather than nesting", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    const menu = await openMenu();

    fireEvent.click(menu.getByRole("button", { name: "Notes" }));

    expect(await screen.findByTestId("notes-popover")).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /more controls/i })).toBeNull();
  });

  it("opens the file dialog from the menu and closes the menu", async () => {
    render(<Recorder onUploaded={() => {}} />);
    const input = screen.getByTestId("upload-input") as HTMLInputElement;
    const click = vi.spyOn(input, "click");
    const menu = await openMenu();

    fireEvent.click(menu.getByRole("button", { name: "Upload" }));

    // openFileDialog is async - it asks for the File System Access picker first and falls back to clicking
    // the hidden input a microtask later, which is the path jsdom takes.
    await waitFor(() => expect(click).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole("dialog", { name: /more controls/i })).toBeNull();
  });

  // Parity with the inline buttons, which is the whole point of the menu: it stands in for them, so a
  // control that is unavailable in the bar must be unavailable here. Without this the menu would hand a
  // user with no CreateRecording a working file picker for an upload the server will refuse.
  it("carries the permission gate onto the auto-stop and upload rows", async () => {
    roomState.can = () => false;
    render(<Recorder onUploaded={() => {}} />);
    const menu = await openMenu();

    for (const name of ["Auto-stop", "Upload"]) {
      const row = menu.getByRole("button", { name });
      expect(row.getAttribute("aria-disabled")).toBe("true");
      expect(row.getAttribute("title")).toMatch(/permission/i);
    }
  });

  // The menu takes the same gate as the inline camera button rather than inventing its own: with no
  // capture area chosen, capturing would throw the shell's area picker up under the cursor.
  it("carries the capture-area gate onto the screenshot row", async () => {
    (window as unknown as { diariz?: unknown }).diariz = {
      canCaptureScreenshot: true,
      captureScreenshot: vi.fn(),
      onScreenshotCaptured: () => () => {},
      hasCaptureArea: () => Promise.resolve(false),
    };
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    const row = (await openMenu()).getByRole("button", { name: "Capture screenshot" });

    expect(row.getAttribute("title")).toBe("Set a capture area first");
    expect(row.getAttribute("aria-disabled")).toBe("true");
  });
});
