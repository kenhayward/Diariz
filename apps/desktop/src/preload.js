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
});
