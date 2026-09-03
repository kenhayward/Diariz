import { render, screen, fireEvent, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import NotesPopout from "./NotesPopout";
import type { NotesState, NotesClientHandlers } from "../lib/notesChannel";

// One client instance shared with the test, so the test can drive the page the way the host would.
const client = {
  add: vi.fn(),
  toggleAutoCapture: vi.fn(),
  edit: vi.fn(),
  remove: vi.fn(),
  removeShot: vi.fn(),
  capture: vi.fn(),
  changeArea: vi.fn(),
  close: vi.fn(),
  dispose: vi.fn(),
};
let handlers: NotesClientHandlers;

vi.mock("../lib/notesChannel", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/notesChannel")>()),
  createNotesClient: (h: NotesClientHandlers) => {
    handlers = h;
    return client;
  },
}));

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [
    { id: "n1", text: "First point", capturedAtMs: 61_000, ordinal: 0, createdAt: "2026-08-13T10:00:00.000Z" },
  ],
  shots: [],
  canCapture: false,
  captureAreaSet: false,
  recording: true,
  ...over,
});

const withTranscript = (startMs: number, text: string, speaker?: string): NotesState =>
  state({
    liveTranscript: {
      recordingId: "rec-1",
      highestSequence: 0,
      segments: [{ id: "s1", startMs, endMs: startMs + 3000, text, sequence: 0, speaker }],
    },
  });

const composer = () => screen.getByLabelText(/note this moment/i) as HTMLInputElement;

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
    expect(screen.queryByText(/waiting for the main/i)).toBeNull();
  });

  it("sends typed text to the host rather than adding it locally", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    fireEvent.change(composer(), { target: { value: "Second point" } });
    fireEvent.keyDown(composer(), { key: "Enter" });

    // No stamp: the host reads its own pause-aware clock, which this window cannot.
    expect(client.add).toHaveBeenCalledWith("Second point", undefined);
    // The host owns the list. Nothing appears here until it publishes the stamped line back.
    expect(screen.queryByText("Second point")).toBeNull();
  });

  it("routes edits and deletes to the host", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    fireEvent.change(screen.getByLabelText(/edit note/i), { target: { value: "Revised" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));
    expect(client.edit).toHaveBeenCalledWith("n1", "Revised");

    fireEvent.click(screen.getByRole("button", { name: /delete note/i }));
    expect(client.remove).toHaveBeenCalledWith("n1");
  });

  // A window opened before its host is ready has not *lost* anything, so it must keep saying it is
  // waiting rather than claiming contact was broken. Found by opening the real window with no host.
  it("keeps waiting, rather than claiming lost contact, when there was never a host", () => {
    render(<NotesPopout />);

    act(() => handlers.onDisconnected());

    expect(screen.getByText(/waiting for the main/i)).toBeTruthy();
    expect(screen.queryByText(/lost contact/i)).toBeNull();
  });

  it("disables the input when the host stops answering", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    act(() => handlers.onDisconnected());

    expect(screen.getByText(/lost contact/i)).toBeTruthy();
    expect(composer().disabled).toBe(true);
  });

  it("comes back to life when the host answers again", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));
    act(() => handlers.onDisconnected());

    act(() => handlers.onState(state()));

    expect(screen.queryByText(/lost contact/i)).toBeNull();
    expect(composer().disabled).toBe(false);
  });

  it("hides the capture controls when the shell cannot capture", () => {
    render(<NotesPopout />);

    act(() => handlers.onState(state({ canCapture: false })));

    expect(screen.queryByRole("button", { name: /capture/i })).toBeNull();
  });

  it("offers capture once the shell can, gated on a capture area being set", () => {
    render(<NotesPopout />);

    // Capturing with no area opens the picker and then sits inert until it settles, which reads as the
    // window having frozen - so "set the area" stays the visible first step, exactly as in the popover.
    // (How that is expressed - inert but hoverable, so the button can say why - is CaptureControls' own
    // test; here we only care that this window passes the gate down.)
    act(() => handlers.onState(state({ canCapture: true, captureAreaSet: false })));
    fireEvent.click(screen.getByRole("button", { name: /capture screenshot/i }));
    expect(client.capture).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /change capture area/i }));
    expect(client.changeArea).toHaveBeenCalledTimes(1);

    act(() => handlers.onState(state({ canCapture: true, captureAreaSet: true })));
    fireEvent.click(screen.getByRole("button", { name: /capture screenshot/i }));
    expect(client.capture).toHaveBeenCalledTimes(1);
  });

  it("tells the host it is closing when the window goes away", () => {
    render(<NotesPopout />);

    window.dispatchEvent(new Event("pagehide"));

    expect(client.close).toHaveBeenCalledTimes(1);
  });

  it("sends a pinned note across with the moment it was pinned to", () => {
    // The pop-out is the window someone uses BECAUSE a call has the screen, so it is the one that most
    // needs to write about something said a moment ago. The host still stamps - it is told which moment.
    render(<NotesPopout />);
    act(() => handlers.onState(withTranscript(20_000, "the warehouse integration")));

    fireEvent.click(screen.getByRole("button", { name: /write a note about this moment/i }));
    fireEvent.change(composer(), { target: { value: "about that" } });
    fireEvent.keyDown(composer(), { key: "Enter" });

    expect(client.add).toHaveBeenCalledWith("about that", 20_000);
  });

  it("shows the live transcript inline, with no tab to reach it", () => {
    // The detached window is the one someone uses precisely BECAUSE a call has the screen, so it is the
    // window that most needs the transcript in front of them rather than one click away.
    render(<NotesPopout />);
    act(() => handlers.onState(withTranscript(0, "shall we make a start", "Ada")));

    expect(screen.queryAllByRole("tab")).toHaveLength(0);
    expect(screen.getByText("shall we make a start")).toBeTruthy();
    expect(screen.getByTestId("stream-speaker").textContent).toBe("Ada");
    // And the note the host sent is on the same list, which is the whole point of the redesign.
    expect(screen.getByText("First point")).toBeTruthy();
  });

  it("shows no status line when the host is not transcribing", () => {
    // A recording started before the host could begin a live capture has no transcript coming, and a
    // green "Live" dot over one that never arrives would be a lie.
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    expect(screen.queryByTestId("live-transcript-status")).toBeNull();
  });

  it("passes the lag and paused state through, so both windows say the same thing", () => {
    // The two windows read from one source. If the pop-out rendered its own idea of the status, the
    // inline panel could say the transcript had paused while the detached one claimed it was keeping up.
    render(<NotesPopout />);
    act(() =>
      handlers.onState(
        state({
          liveTranscript: { recordingId: "rec-1", highestSequence: 0, segments: [] },
          liveDegraded: true,
        }),
      ),
    );

    expect(screen.getByTestId("live-transcript-status").textContent).toMatch(/paused/i);
  });

  it("tells the capture row why it is inert when the host has gone", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state({ canCapture: true, captureAreaSet: true })));

    act(() => handlers.onDisconnected());

    expect(
      screen.getByRole("button", { name: /capture screenshot/i }).getAttribute("title"),
    ).toMatch(/not connected/i);
  });
});

describe("NotesPopout clock", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const clocked = (over: Partial<NonNullable<NotesState["clock"]>> = {}) =>
    state({ clock: { recordedMs: 61_000, atWallMs: Date.now(), running: true, ...over } });

  it("ticks the recorded clock for itself between publishes", () => {
    // Deliberately not driven by the host's publishes: once the main window is hidden to the tray
    // Chromium throttles its timers, so a clock waiting on a broadcast would stutter exactly when this
    // window is the one being looked at.
    render(<NotesPopout />);
    act(() => handlers.onState(clocked()));
    expect(screen.getByTestId("notes-elapsed").textContent).toBe("1:01");

    act(() => vi.advanceTimersByTime(3_000));

    expect(screen.getByTestId("notes-elapsed").textContent).toBe("1:04");
  });

  it("freezes while the recording is paused", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(clocked({ running: false })));

    act(() => vi.advanceTimersByTime(10_000));

    expect(screen.getByTestId("notes-elapsed").textContent).toBe("1:01");
  });

  it("shows a paused clock at the reading it was paused at, however old that reading is", () => {
    // The pause may have started minutes ago - the host does not republish while nothing changes. A
    // paused clock that extrapolated from when the pause began would count the whole pause as meeting
    // time, and every note filed after it would carry a stamp that never happened.
    render(<NotesPopout />);

    act(() =>
      handlers.onState(
        state({ clock: { recordedMs: 61_000, atWallMs: Date.now() - 300_000, running: false } }),
      ),
    );

    expect(screen.getByTestId("notes-elapsed").textContent).toBe("1:01");
  });

  it("takes up the host's fresh reading on resume rather than extrapolating across the pause", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(clocked()));
    act(() => vi.advanceTimersByTime(5_000));

    // The host republishes on resume with the recorded time it actually reached and a new wall moment.
    act(() => handlers.onState(state({ clock: { recordedMs: 66_000, atWallMs: Date.now(), running: true } })));

    expect(screen.getByTestId("notes-elapsed").textContent).toBe("1:06");
  });

  it("shows zero rather than crashing when the host sends no clock at all", () => {
    // An older main window that predates the clock. The notes must still work.
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    expect(screen.getByTestId("notes-elapsed").textContent).toBe("0:00");
  });
});
