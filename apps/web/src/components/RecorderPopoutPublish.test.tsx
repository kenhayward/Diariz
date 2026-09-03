import { render, screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";
import { expectsConsoleError } from "../test-setup";

// A JWT-shaped token whose payload decodes to { sub: "u1" } (used for the per-user pending key).
const TOKEN = `h.${btoa(JSON.stringify({ sub: "u1" }))}.s`;

let hubHandlers: import("../lib/signalr").HubHandlers | null = null;
vi.mock("../lib/signalr", () => ({
  createHub: (...args: unknown[]) => {
    hubHandlers = args[0] as import("../lib/signalr").HubHandlers;
    return hubFactory(...args);
  },
}));
let hubFactory: (...args: unknown[]) => unknown = () => ({
  start: () => Promise.resolve(),
  stop: () => Promise.resolve(),
  on: () => {},
});

vi.mock("../lib/api", () => ({
  api: {
    upload: vi.fn(), createNotes: vi.fn(), createScreenshot: vi.fn(),
    renameRecording: vi.fn(), putCalendarLink: vi.fn(),
    // Live capture defaults to unavailable, so every pre-existing test exercises the fallback
    // path - which is exactly the behaviour that must not change.
    beginLive: vi.fn().mockRejectedValue(new Error("no live")),
    putChunk: vi.fn().mockResolvedValue(undefined),
    finalizeLive: vi.fn().mockResolvedValue(undefined),
    discardLive: vi.fn().mockResolvedValue(undefined),
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
import { onChatLiveRecordingAttached, onChatScreenshotAttached } from "../lib/chatAttachments";

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

/// The recovery banners and the overrun prompt all float over the routed page. A background with an alpha
/// modifier (`bg-amber-900/30`) lets that page's own controls read through the panel and tangle with the
/// banner's buttons, and a button with no background of its own shows the page through the button itself.
/// Asserting on the classes is the only handle jsdom gives us - it computes no compositing (#601).
function expectOpaqueFloatingPanel(panel: HTMLElement) {
  const backgrounds = panel.className.split(/\s+/).filter((c) => /(^|:)bg-/.test(c));
  expect(backgrounds.length).toBeGreaterThan(0);
  for (const cls of backgrounds) expect(cls).not.toMatch(/\//);
  for (const button of Array.from(panel.querySelectorAll("button")))
    expect(button.className).toMatch(/(^|\s)(dark:)?bg-/);
}

// Live capture defaults to unavailable for every test in this file, so the whole pre-existing suite
// exercises the fallback path - the behaviour that must not change. `vi.clearAllMocks` in the various
// beforeEach hooks resets calls but keeps implementations, so a test that makes live capture succeed
// would otherwise leak into every test after it, in any describe block.

/// The host's half of the pop-out transcript.
///
/// NotesPopout's own tests prove the detached window renders what it is handed. Nothing proved the
/// recorder hands it over, and the gap was not hypothetical: replacing the published `liveDegraded`
/// with a literal `false` passed every other test in the suite, which means the pop-out could have gone
/// on claiming the transcript was keeping up while the inline panel said it had stopped. One source,
/// two windows - the whole point is that they cannot disagree.
///
/// It lives in its own file because capturing what is published means replacing `useNotesPopout`, and
/// the recorder suite has a test that exercises the real one.
let published: import("../lib/notesChannel").NotesState | null = null;
/// The handlers the recorder gives the pop-out. Captured so a test can drive the RELAY path directly -
/// the pop-out's messages arrive here, not through anything this window renders, so nothing on screen
/// can stand in for them.
let relayed: import("../lib/useNotesPopout").NotesPopoutHandlers | null = null;
vi.mock("../lib/useNotesPopout", () => ({
  useNotesPopout: ({
    state,
    handlers,
  }: {
    state: import("../lib/notesChannel").NotesState;
    handlers: import("../lib/useNotesPopout").NotesPopoutHandlers;
  }) => {
    published = state;
    relayed = handlers;
    return { poppedOut: false, popOut: vi.fn(), notifyClosed: vi.fn() };
  },
}));

beforeEach(() => {
  published = null;
  relayed = null;
  hubHandlers = null;
  hubFactory = () => ({
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    on: () => {},
  });
  (getStream as Mock).mockResolvedValue(fakeSession);
  (api.putChunk as Mock).mockResolvedValue(undefined);
  (api.finalizeLive as Mock).mockResolvedValue(undefined);
  (api.discardLive as Mock).mockResolvedValue(undefined);
});

describe("what the recorder publishes to the pop-out window", () => {
  it("sends the live transcript once a capture has begun", async () => {
    (api.beginLive as Mock).mockResolvedValue({ id: "live-9", sessionId: "s9", status: "Live" });

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    await waitFor(() => expect(published?.liveTranscript?.recordingId).toBe("live-9"));
  });

  it("sends the recorded clock as a reading the pop-out can tick for itself", async () => {
    // A reading plus the wall-clock moment it was taken, never a ticking value. The pop-out extrapolates
    // between publishes, which is what lets its clock stay smooth while Chromium throttles this window's
    // timers to roughly 1 Hz once it is hidden to the tray.
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });

    await waitFor(() => expect(published?.clock).toBeTruthy());
    expect(published!.clock!.running).toBe(true);
    expect(published!.clock!.atWallMs).toBeGreaterThan(0);
    expect(published!.clock!.recordedMs).toBeGreaterThanOrEqual(0);
  });

  it("stops the pop-out's clock while the recording is paused", async () => {
    // The pop-out must not go on counting a meeting that is not being recorded. `running: false` is what
    // freezes it; without it the detached window would drift ahead of the stamp on every note filed.
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    await waitFor(() => expect(published?.clock?.running).toBe(true));

    fireEvent.click(screen.getByRole("button", { name: /pause/i }));

    await waitFor(() => expect(published?.clock?.running).toBe(false));
  });

  it("republishes a fresh reading on resume, so the pop-out does not extrapolate across the gap", async () => {
    // Resuming without a new `atWallMs` would leave the client extrapolating from a reading taken before
    // the pause, adding the whole paused interval to the clock the moment it started again.
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    await waitFor(() => expect(published?.clock?.running).toBe(true));

    fireEvent.click(screen.getByRole("button", { name: /pause/i }));
    await waitFor(() => expect(published?.clock?.running).toBe(false));
    const whilePaused = published!.clock!;

    fireEvent.click(screen.getByRole("button", { name: /resume/i }));

    await waitFor(() => expect(published?.clock?.running).toBe(true));
    expect(published!.clock!.atWallMs).toBeGreaterThanOrEqual(whilePaused.atWallMs);
  });

  it("sends the paused state rather than a hardcoded false", async () => {
    (api.beginLive as Mock).mockResolvedValue({ id: "live-10", sessionId: "s10", status: "Live" });

    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(await screen.findByRole("button", { name: /record/i }));
    await screen.findByRole("button", { name: /^stop$/i });
    await waitFor(() => expect(hubHandlers?.onLiveTranscriptDegraded).toBeTruthy());

    act(() => hubHandlers!.onLiveTranscriptDegraded!({ recordingId: "live-10", sequence: 3 }));

    await waitFor(() => expect(published?.liveDegraded).toBe(true));
  });
});

describe("what the recorder does with the pop-out's chat requests", () => {
  it("sends nothing for a capture the server has not got", async () => {
    // The pop-out decides whether to offer its Chat button from a published snapshot, so it can ask for
    // a capture whose upload had not landed when that snapshot was taken. Acting on it would put an id
    // the server cannot resolve into the chat, and the chat would answer about a capture that is not
    // there. The pop-out's own button is inert for this case; this is the guard behind it.
    (api.beginLive as Mock).mockResolvedValue({ id: "live-11", sessionId: "s11", status: "Live" });
    const attached: unknown[] = [];
    const off = onChatScreenshotAttached((shot) => attached.push(shot));
    try {
      render(<Recorder onUploaded={() => {}} />);
      fireEvent.click(await screen.findByRole("button", { name: /record/i }));
      await screen.findByRole("button", { name: /^stop$/i });
      await waitFor(() => expect(relayed).toBeTruthy());

      act(() => relayed!.onShotToChat("a-capture-that-never-uploaded"));

      expect(attached).toEqual([]);
    } finally {
      off();
    }
  });

  it("relays the running meeting from the pop-out to the chat in this window", async () => {
    (api.beginLive as Mock).mockResolvedValue({ id: "live-12", sessionId: "s12", status: "Live" });
    const attached: string[] = [];
    const off = onChatLiveRecordingAttached((id) => attached.push(id));
    try {
      render(<Recorder onUploaded={() => {}} />);
      fireEvent.click(await screen.findByRole("button", { name: /record/i }));
      await screen.findByRole("button", { name: /^stop$/i });
      await waitFor(() => expect(relayed).toBeTruthy());

      act(() => relayed!.onTranscriptToChat());

      expect(attached).toEqual(["live-12"]);
    } finally {
      off();
    }
  });
});
