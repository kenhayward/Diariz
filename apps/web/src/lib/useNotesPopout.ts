import { useEffect, useRef, useState } from "react";
import { createNotesHost, type NotesHost, type NotesHostHandlers, type NotesState } from "./notesChannel";

/// Everything the host answers except `getState` and `onClientClosed`, which this hook supplies itself.
export type NotesPopoutHandlers = Omit<NotesHostHandlers, "getState" | "onClientClosed">;

/**
 * The main window's half of the pop-out notes window: whether the notes are currently detached, the
 * channel host's lifetime, and republishing whenever the state changes.
 *
 * Nothing is created until the user actually pops out. An idle BroadcastChannel in every session would
 * be pure cost, and detaching is opt-in per recording by design.
 */
export function useNotesPopout({
  state,
  handlers,
  openWindow,
}: {
  state: NotesState;
  handlers: NotesPopoutHandlers;
  /// Asks the shell to open the window. A missing capability is the caller's problem - it should not
  /// offer the control at all in a plain browser.
  openWindow: () => void;
}): { poppedOut: boolean; popOut: () => void; notifyClosed: () => void } {
  const [poppedOut, setPoppedOut] = useState(false);
  const hostRef = useRef<NotesHost | null>(null);

  // The host reads both of these through refs so `getState` always sees the current value without the
  // channel being torn down and rebuilt on every keystroke.
  const stateRef = useRef(state);
  stateRef.current = state;
  const handlersRef = useRef(handlers);
  handlersRef.current = handlers;

  useEffect(() => {
    if (!poppedOut) return;
    const host = createNotesHost({
      getState: () => stateRef.current,
      onAdd: (text, atMs) => handlersRef.current.onAdd(text, atMs),
      onEdit: (id, text) => handlersRef.current.onEdit(id, text),
      onDelete: (id) => handlersRef.current.onDelete(id),
      onDeleteShot: (id) => handlersRef.current.onDeleteShot(id),
      onCapture: () => handlersRef.current.onCapture(),
      onChangeArea: () => handlersRef.current.onChangeArea(),
      onToggleAutoCapture: () => handlersRef.current.onToggleAutoCapture(),
      onShotToChat: (id) => handlersRef.current.onShotToChat(id),
      onTranscriptToChat: () => handlersRef.current.onTranscriptToChat(),
      // Idempotent on purpose: this arrives from the client's own `closing` message AND from the shell
      // noticing the window was destroyed, and either may be first, or missing entirely.
      onClientClosed: () => setPoppedOut(false),
    });
    hostRef.current = host;
    host.publish();
    return () => {
      host.dispose();
      hostRef.current = null;
    };
  }, [poppedOut]);

  // Republish on any change the pop-out renders.
  useEffect(() => {
    if (poppedOut) hostRef.current?.publish();
  }, [poppedOut, state]);

  // The recording is the whole reason the window exists; when it ends, so does the window.
  useEffect(() => {
    if (poppedOut && !state.recording) {
      hostRef.current?.end();
      setPoppedOut(false);
    }
  }, [poppedOut, state.recording]);

  return {
    poppedOut,
    popOut: () => {
      openWindow();
      setPoppedOut(true);
    },
    /// Report the pop-out window gone from outside the channel - the shell noticing it closed. Needed
    /// because a killed renderer never gets to send its own `closing` message. Same idempotent path.
    notifyClosed: () => setPoppedOut(false),
  };
}
