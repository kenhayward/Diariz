"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// The capture-area picker overlay's only job: report one selection, or a cancel.
// Main matches the sender to the display the overlay covers.
contextBridge.exposeInMainWorld("picker", {
  choose: (selection) => ipcRenderer.send("picker:choose", selection),
  cancel: () => ipcRenderer.send("picker:cancel"),
});
