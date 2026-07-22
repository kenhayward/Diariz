"use strict";

const { contextBridge, ipcRenderer } = require("electron");

// Bridge for the screenshot-hotkey settings window.
contextBridge.exposeInMainWorld("hotkeyConfig", {
  load: () => ipcRenderer.invoke("hotkey:load"),
  save: (accelerator) => ipcRenderer.invoke("hotkey:save", accelerator),
  // Formats a raw KeyboardEvent descriptor into Electron's accelerator naming and reports
  // whether it currently validates. Main owns this (via screenshotState.js) because this
  // window is sandboxed and can't require local modules itself.
  describe: (descriptor) => ipcRenderer.invoke("hotkey:describe", descriptor),
});
