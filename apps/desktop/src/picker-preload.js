"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// The capture-area picker overlay's only job: report one selection, or a cancel.
// Main matches the sender to the display the overlay covers.
//
// The overlays are pre-warmed and reused across picks (see pickerPool.js), so main tells
// each one when it has been put away and the next pick may begin - without that the
// overlay's one-shot choose/cancel guard would make every pick after the first inert.
contextBridge.exposeInMainWorld("picker", {
  choose: (selection) => ipcRenderer.send("picker:choose", selection),
  cancel: () => ipcRenderer.send("picker:cancel"),
  onReset: (cb) => ipcRenderer.on("picker:reset", () => cb()),
});
