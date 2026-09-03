/// The pop-out notes window's bridge to the desktop shell (`apps/desktop/src/notes-preload.js`).
///
/// Deliberately separate from `trayRecorder.ts`'s `TrayBridge` and exposed on a different global: the
/// pop-out gets its own narrow preload, because reusing the main one would register a second
/// `onTrayCommand` listener and a tray "Stop Recording" would then drive two recorders.
///
/// Every member is optional and the whole object may be absent - `/notes-popout` is a normal route, so
/// somebody can open it in a browser tab by hand, and every control it gates simply does not render
/// there rather than appearing and doing nothing.

export interface NotesHotkeys {
  /// Already formatted for the platform ("Ctrl+Shift+9" / the mac glyphs), because the shell knows both
  /// the platform and what is actually registered. An empty string means that accelerator could not be
  /// registered, and the hint line leaves it out rather than promising a key that does nothing.
  capture: string;
  note: string;
  transcriptChat: string;
}

export type NotesCommand = { type: "focus-composer" | "transcript-to-chat" };

export interface NotesPopoutBridge {
  isPopout?: boolean;
  /// Float this window above other applications, or stop.
  setAlwaysOnTop?: (flag: boolean) => Promise<{ ok: boolean }>;
  /// Ask the shell to shrink the window to the composer band, or put it back. What to *draw* is this
  /// window's own decision - the shell only resizes.
  setCompact?: (flag: boolean) => Promise<{ ok: boolean }>;
  /// A global hotkey the shell routed here. Returns an unsubscribe function.
  onNotesCommand?: (cb: (cmd: NotesCommand) => void) => () => void;
  loadHotkeys?: () => Promise<NotesHotkeys>;
}

export function notesPopoutBridge(): NotesPopoutBridge | undefined {
  return (window as unknown as { diarizNotes?: NotesPopoutBridge }).diarizNotes;
}
