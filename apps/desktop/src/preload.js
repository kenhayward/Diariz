"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// Minimal, safe surface for the web app. `isElectron` flips on the "System audio"
// recorder option. The web app is loaded from the server origin, so it talks to the
// API same-origin and needs no API-base override.
//
// Tray-driven recording (phase 2): the tray menu sends "start"/"stop" commands to
// the renderer over `onTrayCommand`, and the renderer reports its recorder phase
// back via `reportRecorderState` so the tray can reflect it (label, tooltip,
// notifications). All of this is a no-op in a plain browser (no `window.diariz`).
contextBridge.exposeInMainWorld("diariz", {
  isElectron: true,

  /// Subscribe to tray record/stop commands. `cb` receives { type: "start"|"stop", source? }.
  /// Returns an unsubscribe function.
  onTrayCommand: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("tray:command", listener);
    return () => ipcRenderer.removeListener("tray:command", listener);
  },

  /// Report the recorder phase to the main process so the tray can update.
  /// state: { phase: "idle"|"recording"|"uploading"|"error", source?, error? }.
  reportRecorderState: (state) => ipcRenderer.send("recorder:state", state),

  /// True when this shell can capture screenshots (used to show the capture affordances).
  canCaptureScreenshot: true,

  /// Ask main to capture now. The first capture of a recording opens the area picker.
  captureScreenshot: () => ipcRenderer.invoke("screenshot:capture"),

  /// Forget this recording's capture area and re-open the picker.
  changeCaptureArea: () => ipcRenderer.invoke("screenshot:change-area"),

  /// Whether this recording has a capture area yet (the web app disables its capture button until it does).
  hasCaptureArea: () => ipcRenderer.invoke("screenshot:has-area"),

  /// Subscribe to capture-area changes: `cb` receives true once an area is chosen, false when it is cleared
  /// (a new recording, a re-pick, or a capture that found its display gone). Returns an unsubscribe function.
  onCaptureAreaChanged: (cb) => {
    const listener = (_event, hasArea) => cb(hasArea);
    ipcRenderer.on("screenshot:area-changed", listener);
    return () => ipcRenderer.removeListener("screenshot:area-changed", listener);
  },

  /// Subscribe to captured images. `cb` receives { full, thumb, width, height } where
  /// `full` and `thumb` are Uint8Arrays (PNG and JPEG) - Electron's structured-clone IPC
  /// turns the main process's Node Buffers into Uint8Array on arrival here, not
  /// ArrayBuffer. `new Blob([uint8Array], { type })` accepts a Uint8Array directly, so
  /// no conversion is needed before handing these to the renderer's Blob/URL APIs.
  /// Returns an unsubscribe function.
  onScreenshotCaptured: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("screenshot:captured", listener);
    return () => ipcRenderer.removeListener("screenshot:captured", listener);
  },

  /// Start Google sign-in (opens the system browser; the result returns via onAuthToken).
  startGoogleSignIn: () => ipcRenderer.invoke("auth:start-google"),

  /// Subscribe to a signed-in access token delivered after a diariz:// sign-in deep link.
  /// Returns an unsubscribe function.
  onAuthToken: (cb) => {
    const listener = (_event, token) => cb(token);
    ipcRenderer.on("auth:token", listener);
    return () => ipcRenderer.removeListener("auth:token", listener);
  },

  /// Subscribe to a desktop sign-in FAILURE (reason: "network"|"expired"|"rejected") so the login
  /// screen can show why, instead of silently doing nothing. Returns an unsubscribe function.
  onAuthError: (cb) => {
    const listener = (_event, reason) => cb(reason);
    ipcRenderer.on("auth:error", listener);
    return () => ipcRenderer.removeListener("auth:error", listener);
  },

  // ---- Desktop Outlook calendar ----
  // The shell reads the local calendar; the web app uploads it, because it is the side holding the user's
  // token. Mirrors the screenshot split. See apps/web/src/lib/outlookSync.ts for the consuming interface.

  /// True on Windows, where reading a local Outlook calendar is possible at all. Whether it is actually
  /// reachable (installed, classic rather than the new Outlook) is outlookAvailable().
  canSyncOutlook: process.platform === "win32",

  /// Whether the shell can reach Outlook right now.
  outlookAvailable: () => ipcRenderer.invoke("outlook:available"),

  /// Report the connector's settings. Arriving at all also tells the shell a signed-in renderer is ready to
  /// POST, which is what lets it run its launch sync.
  reportOutlookReady: (cfg) => ipcRenderer.send("outlook:ready", cfg),

  /// Ask for a sync now. Resolves { started, reason? } so the caller can explain a refusal.
  syncOutlookNow: () => ipcRenderer.invoke("outlook:sync-now"),

  /// Subscribe to a harvested window awaiting upload. Returns an unsubscribe function.
  onOutlookPush: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("outlook:push", listener);
    return () => ipcRenderer.removeListener("outlook:push", listener);
  },

  /// Report what the API did with a pushed window, so the tray and its notification are honest.
  reportOutlookResult: (result) => ipcRenderer.send("outlook:result", result),

  /// Subscribe to the shell's sync phase, so on-screen buttons can disable themselves while one runs.
  /// Returns an unsubscribe function.
  onOutlookState: (cb) => {
    const listener = (_event, state) => cb(state);
    ipcRenderer.on("outlook:state", listener);
    return () => ipcRenderer.removeListener("outlook:state", listener);
  },
});
