import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import AudioSourceChip from "./AudioSourceChip";
import RecordHero from "./RecordHero";

// ----------------------------------------------------------------------------------------------------
// Responsive labels (leaf components, no mocks needed)
// ----------------------------------------------------------------------------------------------------
// jsdom has no real layout engine, so a width-driven media query can't be exercised meaningfully here.
// Instead we assert the responsive utility classes are present on the collapsible text labels: the
// "Audio source" chip text and the idle record hero label both hide at narrow widths (mic icon / red
// circle stay), which is what the spec's "drop the text label" behaviour needs. Narrow-width rendering
// itself is verified visually.
describe("hub responsive labels", () => {
  it("hides the Audio source chip text at narrow widths (keeps it from md up)", () => {
    render(<AudioSourceChip systemAudio={false} expanded={false} onClick={() => {}} />);
    const label = screen.getByText("Audio source");
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("md:inline");
  });

  it("hides the idle record hero label at narrow widths (keeps it from md up)", () => {
    render(
      <RecordHero
        recording={false}
        paused={false}
        mmss="00:00"
        stream={null}
        canRecord
        busy={false}
        startDisabled={false}
        onStart={() => {}}
        onPause={() => {}}
        onResume={() => {}}
        onStop={() => {}}
        onSilentChange={() => {}}
      />,
    );
    const label = screen.getByText("Start recording");
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("md:inline");
  });
});

// ----------------------------------------------------------------------------------------------------
// Cross-component integration (real Recorder + UserMenu under one HubPopoverProvider)
// ----------------------------------------------------------------------------------------------------
// These cover the glue that per-component unit tests miss: the shared single-open popover context spans
// two independent components (the recorder cluster and the account avatar), and the record flow reveals
// the Notes control. Boundaries (api / audio / rooms / status / auth) are faked exactly as the Recorder
// and UserMenu unit tests fake them.

const TOKEN = `h.${btoa(JSON.stringify({ sub: "u1" }))}.s`;

const authState = {
  initials: "JD",
  email: "jane.doe@x.com",
  fullName: "Jane Doe",
  pictureUrl: null as string | null,
  isAdmin: false,
  isPlatformAdmin: false,
  canManageFormulas: false,
  logout: vi.fn(),
};

vi.mock("../../auth", () => ({ useAuth: () => authState }));
vi.mock("../../lib/api", () => ({
  api: {
    upload: vi.fn(),
    createNotes: vi.fn(),
    getUserStorage: vi
      .fn()
      .mockResolvedValue({ usedBytes: 1024 ** 3, quotaBytes: 5 * 1024 ** 3, totalTranscriptionMs: 0 }),
  },
  apiErrorMessage: (_e: unknown, fb: string) => fb,
  getToken: () => TOKEN,
}));
vi.mock("../../lib/audioSource", () => ({
  getStream: vi.fn(),
  getCombinedStream: vi.fn(),
  supportsDisplayAudio: vi.fn(() => true),
  isElectron: false,
  describeAudioError: () => "audio error",
  listInputDevices: vi.fn().mockResolvedValue({ devices: [], hasLabels: false }),
  micPermissionState: vi.fn().mockResolvedValue("granted"),
  unlockDeviceLabels: vi.fn().mockResolvedValue(undefined),
}));
vi.mock("../../lib/rooms", () => ({
  useRoom: () => ({
    can: () => true,
    currentRoom: undefined,
    rooms: [],
    permissions: 0,
    selectedSectionId: null,
    recordingSectionId: null,
    isLoading: false,
  }),
}));
vi.mock("../../lib/status", () => ({ useStatus: () => ({ status: null, setStatus: vi.fn() }) }));
vi.mock("../../lib/uploadContext", () => ({ useUpload: () => ({ uploadFiles: vi.fn() }) }));
vi.mock("../../lib/pendingRecording", () => ({
  savePendingRecording: vi.fn().mockResolvedValue(undefined),
  loadPendingRecording: vi.fn().mockResolvedValue(null),
  clearPendingRecording: vi.fn().mockResolvedValue(undefined),
}));
vi.mock("../../lib/pendingNotes", () => ({
  savePendingNotes: vi.fn().mockResolvedValue(undefined),
  loadPendingNotes: vi.fn().mockResolvedValue(null),
  clearPendingNotes: vi.fn().mockResolvedValue(undefined),
}));

import { getStream } from "../../lib/audioSource";
import { HubPopoverProvider } from "./hubPopovers";
import Recorder from "../Recorder";
import UserMenu from "../UserMenu";

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
const fakeSession = { stream: fakeStream, stop: () => {} };

// The full command hub: the recorder cluster and the account avatar under one shared popover context -
// the same wiring TopBar uses (minus the pure frame markup, which TopBar.test covers).
function renderHub() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <HubPopoverProvider>
        <Recorder compact onUploaded={() => {}} />
        <UserMenu />
      </HubPopoverProvider>
    </QueryClientProvider>,
  );
}

describe("TopBar command-hub integration", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (getStream as Mock).mockResolvedValue(fakeSession);
  });

  it("shares one open popover across the recorder and the account menu", async () => {
    renderHub();
    await screen.findByRole("button", { name: /^record$/i });

    // Open the audio-source popover from the recorder cluster.
    fireEvent.click(screen.getByRole("button", { name: /audio source/i }));
    expect(screen.getByTestId("audio-source-popover")).toBeTruthy();

    // Clicking the account avatar (a different component) closes the source popover and opens the account
    // menu - the cross-component single-open behaviour the shared context provides.
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    await waitFor(() => expect(screen.queryByTestId("audio-source-popover")).toBeNull());
    expect(screen.getByRole("dialog", { name: /account/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /preferences/i })).toBeTruthy();

    // ...and back the other way: opening the source popover closes the account menu.
    fireEvent.click(screen.getByRole("button", { name: /audio source/i }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /account/i })).toBeNull());
    expect(screen.getByTestId("audio-source-popover")).toBeTruthy();
  });

  it("shows the Notes button when recording and hides it when idle", async () => {
    renderHub();
    // Idle: the pencil Notes toggle is not rendered.
    await screen.findByRole("button", { name: /^record$/i });
    expect(screen.queryByRole("button", { name: /^notes$/i })).toBeNull();

    // Start recording (mocked getStream): the recording pill (stop) and the Notes toggle appear.
    fireEvent.click(screen.getByRole("button", { name: /^record$/i }));
    expect(await screen.findByRole("button", { name: /^stop$/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /^notes$/i })).toBeTruthy();

    // Stop: the Notes toggle disappears again.
    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    await waitFor(() => expect(screen.queryByRole("button", { name: /^notes$/i })).toBeNull());
  });

  it("closes an open popover on Escape and on a backdrop click", async () => {
    renderHub();
    await screen.findByRole("button", { name: /^record$/i });

    // Escape closes the audio-source popover.
    fireEvent.click(screen.getByRole("button", { name: /audio source/i }));
    expect(screen.getByTestId("audio-source-popover")).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.queryByTestId("audio-source-popover")).toBeNull());

    // Backdrop click closes it too.
    fireEvent.click(screen.getByRole("button", { name: /audio source/i }));
    expect(screen.getByTestId("audio-source-popover")).toBeTruthy();
    fireEvent.click(screen.getByTestId("hub-popover-backdrop"));
    await waitFor(() => expect(screen.queryByTestId("audio-source-popover")).toBeNull());
  });
});
