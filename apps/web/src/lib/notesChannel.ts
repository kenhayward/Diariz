/// The pop-out notes protocol. The main window (the HOST) owns the recorder, the note lines, the
/// screenshot stash and the recorded clock; the pop-out window (the CLIENT) owns nothing but its draft
/// input. Everything the client wants done, it asks the host to do - including stamping a new line,
/// because only the host knows the recorded (pause-aware) clock.
///
/// Transport is BroadcastChannel, which is scoped to the origin - and both windows load the web app
/// from the same server origin, so no extra auth or wiring is involved. A window does not receive its
/// own broadcasts, which is what lets the host publish and listen on one channel object.
///
/// The liveness poll is driven by the CLIENT, which looks backwards until you know why: once the main
/// window is hidden to the tray, Chromium throttles its timers (measured at roughly 1 Hz, and far
/// worse past five minutes hidden), so a host heartbeat would stall exactly when the feature is being
/// used and the pop-out would report a disconnect for a perfectly healthy host. Message *delivery* to a
/// hidden host is not throttled, so its replies stay prompt. The visible window therefore does the
/// polling. See the spike results in docs/superpowers/specs/2026-08-13-notes-popout-window-design.md.

import type { MeetingNote, ShotView } from "./types";

export const NOTES_CHANNEL = "diariz.live-notes";

/// Everything the pop-out renders. Rebuilt by the host on every publish.
export interface NotesState {
  lines: MeetingNote[];
  shots: ShotView[];
  /// Whether this shell can capture screenshots at all. Sent as a flag rather than read from
  /// `window.diariz` in the client: the pop-out's preload deliberately does not expose the capture
  /// bridge, and the host is the authority either way.
  canCapture: boolean;
  captureAreaSet: boolean;
  recording: boolean;
}

type HostMessage = { type: "state"; state: NotesState } | { type: "ended" };

type ClientMessage =
  | { type: "hello" }
  | { type: "ping" }
  | { type: "add"; text: string }
  | { type: "edit"; id: string; text: string }
  | { type: "delete"; id: string }
  | { type: "deleteShot"; id: string }
  | { type: "capture" }
  | { type: "changeArea" }
  | { type: "closing" };

/// The slice of BroadcastChannel used here, so tests can supply a fake.
export interface ChannelLike {
  postMessage(data: unknown): void;
  close(): void;
  onmessage: ((e: { data: unknown }) => void) | null;
}

function open(channel?: ChannelLike): ChannelLike {
  // A real BroadcastChannel satisfies this contract at runtime, but not to the typechecker: its
  // `onmessage` is declared with the full `MessageEvent`, and because that is a property rather than a
  // method its parameter is checked contravariantly - so the narrower `{ data: unknown }` this module
  // actually uses is rejected. Widening ChannelLike to MessageEvent instead would force every test
  // fake to construct one, which is a worse trade for a seam that exists only to be faked.
  return channel ?? (new BroadcastChannel(NOTES_CHANNEL) as unknown as ChannelLike);
}

// ---- Host (the main window) ----

export interface NotesHostHandlers {
  onAdd(text: string): void;
  onEdit(id: string, text: string): void;
  onDelete(id: string): void;
  onDeleteShot(id: string): void;
  onCapture(): void;
  onChangeArea(): void;
  /// The pop-out window is going away; restore the inline popover. Must be idempotent - it arrives from
  /// the client's own `closing` message AND from the shell noticing the window was destroyed, and
  /// either may be first, or missing entirely.
  onClientClosed(): void;
  getState(): NotesState;
}

export interface NotesHost {
  /// Broadcast the current state. Call whenever anything in it changes.
  publish(): void;
  /// Tell the pop-out the recording is over, so it can close itself.
  end(): void;
  dispose(): void;
}

export function createNotesHost(
  handlers: NotesHostHandlers,
  opts: { channel?: ChannelLike } = {},
): NotesHost {
  const ch = open(opts.channel);

  const publish = () => {
    const message: HostMessage = { type: "state", state: handlers.getState() };
    ch.postMessage(message);
  };

  ch.onmessage = (e) => {
    const m = e.data as ClientMessage;
    switch (m.type) {
      // A ping is answered with the full state rather than a bare pong: it costs the same round trip
      // and it self-heals a client that missed an update while the host was busy.
      case "hello":
      case "ping":
        publish();
        break;
      case "add":
        handlers.onAdd(m.text);
        break;
      case "edit":
        handlers.onEdit(m.id, m.text);
        break;
      case "delete":
        handlers.onDelete(m.id);
        break;
      case "deleteShot":
        handlers.onDeleteShot(m.id);
        break;
      case "capture":
        handlers.onCapture();
        break;
      case "changeArea":
        handlers.onChangeArea();
        break;
      case "closing":
        handlers.onClientClosed();
        break;
    }
  };

  return {
    publish,
    end: () => ch.postMessage({ type: "ended" } satisfies HostMessage),
    dispose: () => {
      ch.onmessage = null;
      ch.close();
    },
  };
}

// ---- Client (the pop-out window) ----

export interface NotesClientHandlers {
  onState(state: NotesState): void;
  onEnded(): void;
  /// Fired once when the host stops answering. A later `state` silently reconnects.
  onDisconnected(): void;
}

export interface NotesClient {
  add(text: string): void;
  edit(id: string, text: string): void;
  remove(id: string): void;
  removeShot(id: string): void;
  capture(): void;
  changeArea(): void;
  /// Tell the host this window is going away.
  close(): void;
  dispose(): void;
}

export function createNotesClient(
  handlers: NotesClientHandlers,
  opts: { channel?: ChannelLike; pingMs?: number } = {},
): NotesClient {
  const ch = open(opts.channel);
  const pingMs = opts.pingMs ?? 2_000;
  let missed = 0;
  let reported = false;

  ch.onmessage = (e) => {
    const m = e.data as HostMessage;
    if (m.type === "ended") {
      handlers.onEnded();
      return;
    }
    if (m.type !== "state") return;
    missed = 0;
    reported = false;
    handlers.onState(m.state);
  };

  const send = (m: ClientMessage) => ch.postMessage(m);

  // Three unanswered pings, not one: a single missed round trip during a busy render is not a dead
  // host, and a banner that flickers on every hiccup trains the user to ignore it.
  const timer = setInterval(() => {
    missed += 1;
    if (missed >= 3 && !reported) {
      reported = true;
      handlers.onDisconnected();
    }
    send({ type: "ping" });
  }, pingMs);

  send({ type: "hello" });

  return {
    add: (text) => send({ type: "add", text }),
    edit: (id, text) => send({ type: "edit", id, text }),
    remove: (id) => send({ type: "delete", id }),
    removeShot: (id) => send({ type: "deleteShot", id }),
    capture: () => send({ type: "capture" }),
    changeArea: () => send({ type: "changeArea" }),
    close: () => send({ type: "closing" }),
    dispose: () => {
      clearInterval(timer);
      ch.onmessage = null;
      ch.close();
    },
  };
}
