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

  /// Ask the shell to open the detached live-notes window - a small always-on-top window that can
  /// float over a full-screen call. Resolves { ok }; false when no server address is configured yet.
  /// The two windows then talk directly over a same-origin BroadcastChannel, not through here.
  openNotesPopout: () => ipcRenderer.invoke("notes:open"),

  /// Subscribe to the pop-out window being closed, however it went (its own button, the OS, a crash).
  /// The web app restores the inline notes popover on this. It is the guaranteed signal - the pop-out
  /// also announces itself over the channel, but a killed renderer never gets to.
  /// Returns an unsubscribe function.
  onNotesPopoutClosed: (cb) => {
    const listener = () => cb();
    ipcRenderer.on("notes:closed", listener);
    return () => ipcRenderer.removeListener("notes:closed", listener);
  },

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

  /// Turn auto-capture (capture each time the screen changes) on or off. With no capture area chosen
  /// yet, main opens the picker first - same as `captureScreenshot`.
  toggleAutoCapture: () => ipcRenderer.invoke("screenshot:toggle-auto-capture"),

  /// Subscribe to auto-capture starting and stopping. `cb` receives
  /// `{ active, area }`, where `area` (present only while active) describes what to capture:
  /// `{ displayWidth, displayHeight, crop }` in the target display's physical pixels, `crop` being null
  /// for a whole screen. The renderer opens its own getDisplayMedia stream - main has already granted the
  /// chosen display for it - so the frames never cross IPC. Returns an unsubscribe function.
  ///
  /// Auto-capture can stop without the user asking (the capture area's display went away, the recording
  /// ended), which is why this is an event rather than something the renderer polls.
  onAutoCaptureChanged: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("screenshot:auto-capture-changed", listener);
    return () => ipcRenderer.removeListener("screenshot:auto-capture-changed", listener);
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

  /// Whether the shell can reach Outlook right now. Answered from the registry, never by activating Outlook -
  /// and a definitive "no" is remembered, so this stays cheap and stops prompting to install Office.
  outlookAvailable: () => ipcRenderer.invoke("outlook:available"),

  /// Look again on a machine where Outlook was previously not found. The only thing that clears the
  /// remembered "no"; Preferences offers it, because nothing else should decide to retry on the user's behalf.
  recheckOutlook: () => ipcRenderer.invoke("outlook:recheck"),

  /// Report the connector's settings. Arriving at all also tells the shell a signed-in renderer is ready to
  /// POST, which is what lets it run its launch sync.
  reportOutlookReady: (cfg) => ipcRenderer.send("outlook:ready", cfg),

  /// Ask for a sync now. `{ scope: "today" }` reads only the current local day - the quick sync, seconds
  /// rather than the tens a full mailbox read costs. Resolves { started, reason? } so the caller can explain
  /// a refusal.
  syncOutlookNow: (options) => ipcRenderer.invoke("outlook:sync-now", options),

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
