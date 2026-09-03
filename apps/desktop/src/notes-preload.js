"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// The pop-out notes window's entire bridge.
//
// Deliberately NOT preload.js: that one exposes onTrayCommand, and a second subscriber would mean a
// tray "Stop Recording" driving two recorders. The pop-out needs none of it - it talks to the main
// window over a same-origin BroadcastChannel rather than over IPC, and even its screenshot buttons are
// relayed through the host rather than invoked here, so the shell's capture bridge stays out too.
//
// `isPopout` exists so the web app can tell it is running in this window rather than a browser tab.
contextBridge.exposeInMainWorld("diarizNotes", {
  isPopout: true,

  /// Float this window above other applications, or stop. The window is created always-on-top, so the
  /// renderer seeds its toggle to true.
  setAlwaysOnTop: (flag) => ipcRenderer.invoke("notes:set-always-on-top", flag),

  /// Shrink the window to its composer band, or restore it. Compact is RENDERER state - it decides what
  /// to draw, because "just the composer band" is a layout question only it can answer - and this asks
  /// the shell for a matching window size.
  setCompact: (flag) => ipcRenderer.invoke("notes:set-compact", flag),

  /// Subscribe to a global-hotkey command routed to this window (the note hotkey, when the notes are
  /// detached). Returns an unsubscribe function.
  onNotesCommand: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("notes:command", listener);
    return () => ipcRenderer.removeListener("notes:command", listener);
  },

  /// The accelerators actually registered, formatted for this platform - the same hint line as the
  /// inline panel, reading the same source.
  loadHotkeys: () => ipcRenderer.invoke("hotkeys:load"),
});
