// Bridge between the Electron tray and the web app's recorder. The desktop preload
// injects `window.diariz` with `onTrayCommand`/`reportRecorderState`; in a plain
// browser those are absent and everything here degrades to a no-op.
//
// Flow: the tray sends { type: "start", source } / { type: "stop" }; we drive the
// same recorder the on-screen button uses, and report phase changes back so the tray
// can show "Stop Recording (mm:ss)", a tooltip, and native notifications.

import type { AudioSourceKind } from "./audioSource";

export type TrayCommand =
  | { type: "start"; source: AudioSourceKind }
  | { type: "stop" };

export type RecorderPhase = "idle" | "recording" | "uploading" | "error";

export interface RecorderState {
  phase: RecorderPhase;
  source?: AudioSourceKind;
  error?: string;
  ready?: boolean;
}

export interface TrayBridge {
  isElectron?: boolean;
  onTrayCommand?: (cb: (cmd: TrayCommand) => void) => () => void;
  reportRecorderState?: (state: RecorderState) => void;
}

export interface TrayRecorderHandlers {
  onStart: (source: AudioSourceKind) => void;
  onStop: () => void;
}

export interface TrayRecorderConnection {
  reportState: (state: RecorderState) => void;
  dispose: () => void;
}

export function connectTrayRecorder(
  bridge: TrayBridge | undefined,
  handlers: TrayRecorderHandlers,
): TrayRecorderConnection {
  const reportState = (state: RecorderState) => bridge?.reportRecorderState?.(state);

  if (!bridge?.onTrayCommand) {
    return { reportState, dispose: () => {} };
  }

  const unsubscribe = bridge.onTrayCommand((cmd) => {
    if (cmd.type === "start") handlers.onStart(cmd.source);
    else if (cmd.type === "stop") handlers.onStop();
  });

  // Now that the tray can drive us, tell it we're ready.
  reportState({ phase: "idle", ready: true });

  return { reportState, dispose: unsubscribe };
}
