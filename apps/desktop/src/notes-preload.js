"use strict";

const { contextBridge } = require("electron");

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
});
