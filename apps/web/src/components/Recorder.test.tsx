import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

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
  });
});

describe("Recorder source selection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
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

    await screen.findByRole("option", { name: "USB Headset" });
    const select = screen.getByRole("combobox");
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

    // Wait for the async device load to populate the options before selecting — otherwise a slow
    // runner can click Record before the label is known, resolving the source without its label.
    await screen.findByRole("option", { name: "USB Headset" });
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "dev:bbb" } });
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

    const select = (await screen.findByRole("combobox")) as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe("dev:bbb"));
  });

  it("feeds cog constraint changes into capture", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    (getStream as Mock).mockResolvedValue(fakeSession);
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /audio settings/i }));
    fireEvent.click(screen.getByRole("checkbox", { name: /noise suppression/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));

    await waitFor(() =>
      expect(getStream).toHaveBeenCalledWith(
        { kind: "default" },
        expect.objectContaining({ noiseSuppression: false }),
      ),
    );
  });

  it("disables the audio-settings cog when No Microphone is selected", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.change(await screen.findByRole("combobox"), { target: { value: "none" } });
    const cog = screen.getByRole("button", { name: /audio settings/i }) as HTMLButtonElement;
    expect(cog.disabled).toBe(true);
  });

  it("shows the System audio checkbox where supported", async () => {
    render(<Recorder onUploaded={() => {}} />);
    expect(await screen.findByRole("checkbox", { name: /system audio/i })).toBeTruthy();
  });

  it("records mic only by default (checkbox off) -> source Microphone, no combined capture", async () => {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByText(/●/);
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Microphone");
    expect(getCombinedStream).not.toHaveBeenCalled();
  });

  it("mixes system audio when a mic is selected and the checkbox is ticked -> source Combined", async () => {
    (getCombinedStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/●/);
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect(getCombinedStream).toHaveBeenCalled();
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Combined");
  });

  it("records system only when No Microphone + system audio -> source System", async () => {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.change(await screen.findByRole("combobox"), { target: { value: "none" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/●/);
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("System");
    expect(getStream).toHaveBeenCalledWith({ kind: "system" }, undefined);
    expect(getCombinedStream).not.toHaveBeenCalled();
  });

  it("disables Record when No Microphone and system audio is off", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.change(await screen.findByRole("combobox"), { target: { value: "none" } });
    expect((screen.getByRole("button", { name: /record/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("falls back to microphone-only when system audio isn't shared", async () => {
    (getCombinedStream as Mock).mockRejectedValue(
      Object.assign(new Error("x"), { name: "NotAllowedError" }),
    );
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("checkbox", { name: /system audio/i }));
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/●/); // still recording (mic only)
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(api.upload).toHaveBeenCalled());
    expect((api.upload as Mock).mock.calls[0][3]).toBe("Microphone");
    expect(screen.getByText(/microphone only/i)).toBeTruthy();
  });

  it("closes the audio-settings popover on an outside click", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /audio settings/i }));
    expect(screen.getByRole("checkbox", { name: /mono/i })).toBeTruthy();

    fireEvent.mouseDown(document.body);
    await waitFor(() => expect(screen.queryByRole("checkbox", { name: /mono/i })).toBeNull());
  });

  it("closes the audio-settings popover via its close button", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: true });
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /audio settings/i }));
    fireEvent.click(screen.getByRole("button", { name: /close/i }));
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

    expect(await screen.findByRole("option", { name: "USB Headset" })).toBeTruthy();
  });

  it("requests mic access when the picker is focused and labels aren't available yet", async () => {
    (listInputDevices as Mock)
      .mockResolvedValueOnce({ devices: [], hasLabels: false }) // mount: no labels yet
      .mockResolvedValue({ devices: [{ deviceId: "bbb", label: "USB Headset" }], hasLabels: true }); // after grant
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.focus(await screen.findByRole("combobox"));

    expect(await screen.findByRole("option", { name: "USB Headset" })).toBeTruthy();
    expect(unlockDeviceLabels).toHaveBeenCalled();
  });

  it("shows a no-microphone hint when focusing the picker but access can't be granted", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
    (unlockDeviceLabels as Mock).mockRejectedValue(
      Object.assign(new Error("x"), { name: "NotFoundError" }),
    );
    render(<Recorder onUploaded={() => {}} />);

    fireEvent.focus(await screen.findByRole("combobox"));

    expect(await screen.findByText(/no microphone detected/i)).toBeTruthy();
  });

  it("no longer shows the bespoke 'allow microphone' link", async () => {
    (listInputDevices as Mock).mockResolvedValue({ devices: [], hasLabels: false });
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("combobox");
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
