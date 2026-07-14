import { render, screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";

// A JWT-shaped token whose payload decodes to { sub: "u1" } (used for the per-user pending key).
const TOKEN = `h.${btoa(JSON.stringify({ sub: "u1" }))}.s`;

vi.mock("../lib/api", () => ({
  api: { upload: vi.fn(), createNotes: vi.fn() },
  apiErrorMessage: (_e: unknown, fb: string) => fb,
  getToken: () => TOKEN,
}));
vi.mock("../lib/pendingNotes", () => ({
  savePendingNotes: vi.fn().mockResolvedValue(undefined),
  loadPendingNotes: vi.fn().mockResolvedValue(null),
  clearPendingNotes: vi.fn().mockResolvedValue(undefined),
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
import Recorder from "./Recorder";

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
      expect(api.upload).toHaveBeenCalledWith(pending.blob, pending.title, pending.durationMs, "Microphone"),
    );
    await waitFor(() => expect(clearPendingRecording).toHaveBeenCalledWith("u1"));
    expect(onUploaded).toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull());
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
        { echoCancellation: true, noiseSuppression: true, autoGainControl: true, mono: true },
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
    fireEvent.click(screen.getByRole("checkbox", { name: /noise suppression/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));

    await waitFor(() =>
      expect(getStream).toHaveBeenCalledWith(
        { kind: "default" },
        expect.objectContaining({ noiseSuppression: false }),
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
