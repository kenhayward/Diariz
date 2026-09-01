import { render, screen, fireEvent, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import NotesPopout from "./NotesPopout";
import type { NotesState, NotesClientHandlers } from "../lib/notesChannel";

// One client instance shared with the test, so the test can drive the page the way the host would.
const client = {
  add: vi.fn(),
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

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "Second point" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(client.add).toHaveBeenCalledWith("Second point");
    // The host owns the list. Nothing appears here until it publishes the stamped line back.
    expect(screen.queryByText("Second point")).toBeNull();
  });

  it("routes edits and deletes to the host", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    fireEvent.click(screen.getByRole("button", { name: /edit/i }));
    fireEvent.change(screen.getByLabelText(/edit/i), { target: { value: "Revised" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));
    expect(client.edit).toHaveBeenCalledWith("n1", "Revised");

    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
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
    expect((screen.getByPlaceholderText(/add a note/i) as HTMLInputElement).disabled).toBe(true);
  });

  it("comes back to life when the host answers again", () => {
    render(<NotesPopout />);
    act(() => handlers.onState(state()));
    act(() => handlers.onDisconnected());

    act(() => handlers.onState(state()));

    expect(screen.queryByText(/lost contact/i)).toBeNull();
    expect((screen.getByPlaceholderText(/add a note/i) as HTMLInputElement).disabled).toBe(false);
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

  it("shows the live transcript in a tab, like the inline panel does", () => {
    // The detached window is the one someone uses precisely BECAUSE a call has the screen, so it is the
    // window that most needs the transcript - and it was the only one without it.
    render(<NotesPopout />);
    act(() =>
      handlers.onState(
        state({
          liveTranscript: {
            recordingId: "rec-1",
            highestSequence: 0,
            segments: [
              { id: "s1", startMs: 0, endMs: 3000, text: "shall we make a start", sequence: 0, speaker: "Ada" },
            ],
          },
        }),
      ),
    );

    fireEvent.click(screen.getByRole("tab", { name: /transcript/i }));

    expect(screen.getByText("shall we make a start")).toBeTruthy();
    expect(screen.getByTestId("live-transcript-speaker").textContent).toBe("Ada");
  });

  it("shows no transcript tab when the host is not sending one", () => {
    // A recording started before the host could begin a live capture has no transcript to show, and an
    // empty tab would suggest one is coming when none is.
    render(<NotesPopout />);
    act(() => handlers.onState(state()));

    expect(screen.queryByRole("tab", { name: /transcript/i })).toBeNull();
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

    fireEvent.click(screen.getByRole("tab", { name: /transcript/i }));
    expect(screen.getByTestId("live-transcript-status").textContent).toMatch(/paused/i);
  });
});
