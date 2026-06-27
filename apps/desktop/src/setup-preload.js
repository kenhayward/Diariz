"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// Bridge for the first-run / settings window.
contextBridge.exposeInMainWorld("setup", {
  getCurrent: () => ipcRenderer.invoke("setup:get-current"),
  save: (url) => ipcRenderer.invoke("setup:save", url),
});
