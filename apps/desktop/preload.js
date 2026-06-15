const { contextBridge } = require("electron");

// Expose a minimal, safe surface to the web app. `isElectron` flips on the
// "System audio" capture option; `__DIARIZ_API_BASE__` lets the bundled web app
// reach the API when not served behind the Vite dev proxy.
contextBridge.exposeInMainWorld("diariz", { isElectron: true });
contextBridge.exposeInMainWorld(
  "__DIARIZ_API_BASE__",
  process.env.DIARIZ_API_BASE || (process.env.DIARIZ_DEV ? "" : "http://localhost:8080"),
);
