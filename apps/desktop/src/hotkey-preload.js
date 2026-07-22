"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// Bridge for the screenshot-hotkey settings window.
contextBridge.exposeInMainWorld("hotkeyConfig", {
  load: () => ipcRenderer.invoke("hotkey:load"),
  save: (accelerator) => ipcRenderer.invoke("hotkey:save", accelerator),
});
