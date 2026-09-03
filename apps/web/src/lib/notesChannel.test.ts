import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createNotesHost, createNotesClient, type ChannelLike, type NotesState } from "./notesChannel";
import type { MeetingNote } from "./types";

/// A fake BroadcastChannel bus: delivers to every OTHER channel on the bus and never to the sender.
/// Those are the semantics the Electron spike confirmed, and the host relies on the "never to the
/// sender" half - it publishes and listens on one channel object, so an echo would have it
/// reprocessing the state it just sent.
function makeBus() {
  const channels: FakeChannel[] = [];
  class FakeChannel implements ChannelLike {
    onmessage: ((e: { data: unknown }) => void) | null = null;
    closed = false;
    postMessage(data: unknown) {
      for (const c of channels) if (c !== this && !c.closed) c.onmessage?.({ data });
    }
    close() {
      this.closed = true;
    }
  }
  return () => {
    const c = new FakeChannel();
    channels.push(c);
    return c;
  };
}

const line = (over: Partial<MeetingNote> = {}): MeetingNote => ({
  id: "n1",
  text: "hello",
  capturedAtMs: 1_000,
  ordinal: 0,
  createdAt: "2026-08-13T10:00:00.000Z",
  ...over,
});

const state = (over: Partial<NotesState> = {}): NotesState => ({
  lines: [line()],
  shots: [],
  canCapture: false,
  captureAreaSet: false,
  recording: true,
  ...over,
});

const noopHandlers = {
  onAdd: vi.fn(),
  onEdit: vi.fn(),
  onDelete: vi.fn(),
  onDeleteShot: vi.fn(),
  onCapture: vi.fn(),
  onChangeArea: vi.fn(),
  onClientClosed: vi.fn(),
};

beforeEach(() => {
  vi.useFakeTimers();
  vi.clearAllMocks();
});
afterEach(() => {
  vi.useRealTimers();
});

describe("notesChannel", () => {
  it("answers a client's hello with the current state", () => {
    const channel = makeBus();
    const current = state();
    createNotesHost({ ...noopHandlers, getState: () => current }, { channel: channel() });
    const onState = vi.fn();
    createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    expect(onState).toHaveBeenCalledWith(current);
  });

  it("renders nothing to a client whose host is absent", () => {
    const channel = makeBus();
    const onState = vi.fn();
    createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    expect(onState).not.toHaveBeenCalled();
  });

  it("routes a client's add to the host, and republishes the stamped result", () => {
    const channel = makeBus();
    let lines = [line()];
    // The host is what stamps the line - the client sends only text.
    const onAdd = vi.fn((text: string) => {
      lines = [...lines, line({ id: "n2", text, capturedAtMs: 42_000 })];
    });
    const host = createNotesHost(
      { ...noopHandlers, onAdd, getState: () => state({ lines }) },
      { channel: channel() },
    );
    const onState = vi.fn();
    const client = createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    client.add("typed in the pop-out");
    host.publish();

    expect(onAdd).toHaveBeenCalledWith("typed in the pop-out", undefined);
    expect(onState).toHaveBeenLastCalledWith(state({ lines }));
    expect(lines[1].capturedAtMs).toBe(42_000);
  });

  it("carries a pinned stamp across to the host", () => {
    // The pop-out can pin a note to a transcript line said earlier. The host still does the stamping -
    // it just gets told which moment to use instead of reading the clock.
    const channel = makeBus();
    const handlers = { ...noopHandlers, getState: () => state() };
    createNotesHost(handlers, { channel: channel() });
    const client = createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() },
      { channel: channel() },
    );

    client.add("about that earlier point", 20_000);

    expect(handlers.onAdd).toHaveBeenCalledWith("about that earlier point", 20_000);
  });

  it("sends no stamp at all when the composer is following the clock", () => {
    // An unpinned note must reach the host with nothing in the stamp slot, so the host reads its own
    // recorded clock. A zero here would file every ordinary note at the start of the meeting.
    const channel = makeBus();
    const handlers = { ...noopHandlers, getState: () => state() };
    createNotesHost(handlers, { channel: channel() });
    const client = createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() },
      { channel: channel() },
    );

    client.add("just now");

    expect(handlers.onAdd).toHaveBeenCalledWith("just now", undefined);
  });

  it("publishes a clock the client can tick for itself", () => {
    // Deliberately not a ticking `elapsedMs`. The host republishes the WHOLE state - thumbnail blobs
    // included - on every change, so a stamp that moved four times a second would rebroadcast the
    // meeting's captures four times a second for its whole length. The client is handed a reading and
    // the wall-clock moment it was taken, and derives the rest.
    const channel = makeBus();
    const clock = { recordedMs: 61_000, atWallMs: 1_700_000_000_000, running: true };
    createNotesHost({ ...noopHandlers, getState: () => state({ clock }) }, { channel: channel() });
    const onState = vi.fn();
    createNotesClient({ onState, onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

    expect(onState.mock.calls[0][0].clock).toEqual(clock);
  });

  it("routes edit, delete, capture and area commands to the host", () => {
    const channel = makeBus();
    const handlers = { ...noopHandlers, getState: () => state() };
    createNotesHost(handlers, { channel: channel() });
    const client = createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() },
      { channel: channel() },
    );

    client.edit("n1", "revised");
    client.remove("n1");
    client.removeShot("s1");
    client.capture();
    client.changeArea();

    expect(handlers.onEdit).toHaveBeenCalledWith("n1", "revised");
    expect(handlers.onDelete).toHaveBeenCalledWith("n1");
    expect(handlers.onDeleteShot).toHaveBeenCalledWith("s1");
    expect(handlers.onCapture).toHaveBeenCalledTimes(1);
    expect(handlers.onChangeArea).toHaveBeenCalledTimes(1);
  });

  it("reports a disconnect after three unanswered pings", () => {
    const channel = makeBus();
    const onDisconnected = vi.fn();
    // No host on the bus at all.
    createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected },
      { channel: channel(), pingMs: 2_000 },
    );

    vi.advanceTimersByTime(2_000);
    expect(onDisconnected).not.toHaveBeenCalled();
    vi.advanceTimersByTime(4_000);
    expect(onDisconnected).toHaveBeenCalledTimes(1);
  });

  it("stays connected while a host answers the pings", () => {
    const channel = makeBus();
    createNotesHost({ ...noopHandlers, getState: () => state() }, { channel: channel() });
    const onDisconnected = vi.fn();
    createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected },
      { channel: channel(), pingMs: 2_000 },
    );

    vi.advanceTimersByTime(20_000);

    expect(onDisconnected).not.toHaveBeenCalled();
  });

  it("does not report the same disconnect over and over", () => {
    const channel = makeBus();
    const onDisconnected = vi.fn();
    createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected },
      { channel: channel(), pingMs: 2_000 },
    );

    vi.advanceTimersByTime(60_000);

    expect(onDisconnected).toHaveBeenCalledTimes(1);
  });

  it("tells the client when the recording ended", () => {
    const channel = makeBus();
    const host = createNotesHost({ ...noopHandlers, getState: () => state() }, { channel: channel() });
    const onEnded = vi.fn();
    createNotesClient({ onState: vi.fn(), onEnded, onDisconnected: vi.fn() }, { channel: channel() });

    host.end();

    expect(onEnded).toHaveBeenCalledTimes(1);
  });

  it("tells the host when the client window is closing", () => {
    const channel = makeBus();
    const onClientClosed = vi.fn();
    createNotesHost({ ...noopHandlers, onClientClosed, getState: () => state() }, { channel: channel() });
    const client = createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() },
      { channel: channel() },
    );

    client.close();

    expect(onClientClosed).toHaveBeenCalledTimes(1);
  });

  it("stops polling once the client is disposed", () => {
    const channel = makeBus();
    const hostChannel = channel();
    const seen: unknown[] = [];
    hostChannel.onmessage = (e) => seen.push(e.data);
    const client = createNotesClient(
      { onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() },
      { channel: channel(), pingMs: 2_000 },
    );

    client.dispose();
    vi.advanceTimersByTime(20_000);

    // Only the initial hello, sent before disposal.
    expect(seen).toEqual([{ type: "hello" }]);
  });
});
