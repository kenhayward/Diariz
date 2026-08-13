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
    act(() => handlers.onState(state({ canCapture: true, captureAreaSet: false })));
    expect((screen.getByRole("button", { name: /capture screenshot/i }) as HTMLButtonElement).disabled).toBe(true);

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
});
