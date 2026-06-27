"use strict";

const { contextBridge } = require("electron");

// Minimal, safe surface for the web app. `isElectron` flips on the "System audio"
// recorder option. The web app is loaded from the server origin, so it talks to the
// API same-origin and needs no API-base override.
contextBridge.exposeInMainWorld("diariz", { isElectron: true });
