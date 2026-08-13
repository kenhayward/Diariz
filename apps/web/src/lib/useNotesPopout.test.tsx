import { render, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { useNotesPopout } from "./useNotesPopout";
import type { NotesState, NotesHostHandlers } from "./notesChannel";

const host = { publish: vi.fn(), end: vi.fn(), dispose: vi.fn() };
let captured: NotesHostHandlers;

vi.mock("./notesChannel", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./notesChannel")>()),
  createNotesHost: (h: NotesHostHandlers) => {
    captured = h;
    return host;
  },
}));

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [],
  shots: [],
  canCapture: false,
  captureAreaSet: false,
  recording: true,
  ...over,
});

const handlers = () => ({
  onAdd: vi.fn(),
  onEdit: vi.fn(),
  onDelete: vi.fn(),
  onDeleteShot: vi.fn(),
  onCapture: vi.fn(),
  onChangeArea: vi.fn(),
});

/// Exposes the hook's return value to the test through a button label and two escape hatches.
let notify: () => void = () => {};
function Harness({
  state: s,
  openWindow,
  on = handlers(),
}: {
  state: NotesState;
  openWindow: () => void;
  on?: ReturnType<typeof handlers>;
}) {
  const { poppedOut, popOut, notifyClosed } = useNotesPopout({ state: s, openWindow, handlers: on });
  notify = notifyClosed;
  return (
    <button type="button" onClick={popOut}>
      {poppedOut ? "out" : "in"}
    </button>
  );
}

beforeEach(() => vi.clearAllMocks());

describe("useNotesPopout", () => {
  it("does not open a channel until the user pops out", () => {
    render(<Harness state={state()} openWindow={vi.fn()} />);

    expect(host.publish).not.toHaveBeenCalled();
  });

  it("opens the shell window and starts hosting on popOut", () => {
    const openWindow = vi.fn();
    const { getByRole } = render(<Harness state={state()} openWindow={openWindow} />);

    act(() => getByRole("button").click());

    expect(openWindow).toHaveBeenCalledTimes(1);
    expect(host.publish).toHaveBeenCalled();
    expect(getByRole("button").textContent).toBe("out");
  });

  it("republishes when the state changes", () => {
    const { getByRole, rerender } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    const before = host.publish.mock.calls.length;

    rerender(<Harness state={state({ captureAreaSet: true })} openWindow={vi.fn()} />);

    expect(host.publish.mock.calls.length).toBeGreaterThan(before);
  });

  it("passes the client's commands through to the recorder's handlers", () => {
    const on = handlers();
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} on={on} />);
    act(() => getByRole("button").click());

    act(() => {
      captured.onAdd("from the pop-out");
      captured.onEdit("n1", "revised");
      captured.onDelete("n1");
      captured.onDeleteShot("s1");
      captured.onCapture();
      captured.onChangeArea();
    });

    expect(on.onAdd).toHaveBeenCalledWith("from the pop-out");
    expect(on.onEdit).toHaveBeenCalledWith("n1", "revised");
    expect(on.onDelete).toHaveBeenCalledWith("n1");
    expect(on.onDeleteShot).toHaveBeenCalledWith("s1");
    expect(on.onCapture).toHaveBeenCalledTimes(1);
    expect(on.onChangeArea).toHaveBeenCalledTimes(1);
  });

  it("reads the current state, not the one captured when hosting began", () => {
    const { getByRole, rerender } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());

    const later = state({ captureAreaSet: true });
    rerender(<Harness state={later} openWindow={vi.fn()} />);

    expect(captured.getState()).toEqual(later);
  });

  it("ends the session and comes back inline when the recording stops", () => {
    const { getByRole, rerender } = render(<Harness state={state({ recording: true })} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());

    rerender(<Harness state={state({ recording: false })} openWindow={vi.fn()} />);

    expect(host.end).toHaveBeenCalledTimes(1);
    expect(getByRole("button").textContent).toBe("in");
  });

  it("comes back inline when the pop-out window closes", () => {
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());

    act(() => captured.onClientClosed());

    expect(getByRole("button").textContent).toBe("in");
    expect(host.dispose).toHaveBeenCalled();
  });

  it("survives the close being reported twice", () => {
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());

    act(() => {
      captured.onClientClosed();
      captured.onClientClosed();
    });

    expect(getByRole("button").textContent).toBe("in");
  });

  // The shell reports the closed window over IPC, which is the only signal that survives the pop-out's
  // renderer being killed outright - its own "closing" message would never be sent.
  it("comes back inline when the shell reports the window gone", () => {
    const { getByRole } = render(<Harness state={state()} openWindow={vi.fn()} />);
    act(() => getByRole("button").click());
    expect(getByRole("button").textContent).toBe("out");

    act(() => notify());

    expect(getByRole("button").textContent).toBe("in");
  });
});
