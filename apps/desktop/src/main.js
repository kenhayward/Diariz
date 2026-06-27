"use strict";

const path = require("node:path");
const { app, BrowserWindow, Tray, Menu, session, desktopCapturer, ipcMain, shell, nativeImage } = require("electron");
const Store = require("electron-store");
const { normalizeServerUrl } = require("./url");

// In dev we load the Vite dev server directly and skip first-run setup.
const DEV_URL = process.env.DIARIZ_DEV ? "http://localhost:5173" : null;

const store = new Store({ name: "diariz-config" });
const ICON = nativeImage.createFromPath(path.join(__dirname, "..", "build", "icon.png"));

let tray = null;
let mainWindow = null;
let setupWindow = null;
let isQuitting = false;

/// The origin the web app is loaded from (dev server, or the configured server).
function targetUrl() {
  return DEV_URL || store.get("serverUrl") || null;
}

// ---- Main window (loads the web app from the server origin) ----

function createMainWindow(url) {
  mainWindow = new BrowserWindow({
    width: 1100,
    height: 800,
    show: true,
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });

  // Grant system-audio (loopback) capture for getDisplayMedia. On Windows,
  // `audio: 'loopback'` records what the system is playing; a screen video source
  // must be supplied even though the renderer discards the video track.
  mainWindow.webContents.session.setDisplayMediaRequestHandler(
    (_request, callback) => {
      desktopCapturer
        .getSources({ types: ["screen"] })
        .then((sources) => callback({ video: sources[0], audio: "loopback" }))
        .catch(() => callback({}));
    },
    { useSystemPicker: false },
  );

  const origin = new URL(url).origin;
  // Open external links in the system browser; keep navigation within the server origin.
  mainWindow.webContents.setWindowOpenHandler(({ url: target }) => {
    shell.openExternal(target);
    return { action: "deny" };
  });
  mainWindow.webContents.on("will-navigate", (e, target) => {
    if (new URL(target).origin !== origin) {
      e.preventDefault();
      shell.openExternal(target);
    }
  });

  // Close to tray rather than quitting (this is a tray-resident app).
  mainWindow.on("close", (e) => {
    if (!isQuitting) {
      e.preventDefault();
      mainWindow.hide();
    }
  });
  mainWindow.on("closed", () => {
    mainWindow = null;
  });

  mainWindow.loadURL(url);
  if (DEV_URL) mainWindow.webContents.openDevTools({ mode: "detach" });
  return mainWindow;
}

function showMainWindow() {
  const url = targetUrl();
  if (!url) {
    showSetupWindow();
    return;
  }
  if (!mainWindow) createMainWindow(url);
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
}

// ---- First-run / settings: server address ----

function showSetupWindow() {
  if (setupWindow) {
    setupWindow.focus();
    return;
  }
  setupWindow = new BrowserWindow({
    width: 460,
    height: 320,
    resizable: false,
    title: "Diariz — Connect",
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "setup-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  setupWindow.setMenuBarVisibility(false);
  setupWindow.loadFile(path.join(__dirname, "setup.html"));
  setupWindow.on("closed", () => {
    setupWindow = null;
  });
}

/// Validate an address by asking its API for /health (which reports the Diariz version),
/// then persist it. Returns { ok } or { ok:false, error }.
async function validateAndSave(rawUrl) {
  const origin = normalizeServerUrl(rawUrl);
  if (!origin) return { ok: false, error: "Enter a valid address, e.g. https://diariz.example.com" };

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 6000);
    const res = await fetch(`${origin}/health`, { signal: controller.signal });
    clearTimeout(timer);
    if (!res.ok) return { ok: false, error: `Server responded ${res.status}. Is this a Diariz server?` };
    await res.json().catch(() => ({})); // /health returns JSON; tolerate shape changes
  } catch {
    return { ok: false, error: "Couldn't reach that server. Check the address and that it's running." };
  }

  store.set("serverUrl", origin);
  return { ok: true, origin };
}

ipcMain.handle("setup:get-current", () => store.get("serverUrl") || "");
ipcMain.handle("setup:save", async (_e, rawUrl) => {
  const result = await validateAndSave(rawUrl);
  if (result.ok) {
    if (setupWindow) setupWindow.close();
    // (Re)load the main window at the new origin.
    if (mainWindow) mainWindow.loadURL(result.origin);
    showMainWindow();
  }
  return result;
});

// ---- Tray ----

function buildTray() {
  const trayIcon = ICON.isEmpty() ? ICON : ICON.resize({ width: 16, height: 16 });
  tray = new Tray(trayIcon);
  tray.setToolTip("Diariz");
  const menu = Menu.buildFromTemplate([
    { label: "Open Diariz", click: () => showMainWindow() },
    { label: "Settings…", click: () => showSetupWindow() },
    { type: "separator" },
    { label: "Quit", click: () => { isQuitting = true; app.quit(); } },
  ]);
  tray.setContextMenu(menu);
  tray.on("click", () => showMainWindow());
}

// ---- App lifecycle ----

// Single-instance: a second launch focuses the running app instead of starting another.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => showMainWindow());

  app.whenReady().then(() => {
    buildTray();
    if (targetUrl()) createMainWindow(targetUrl());
    else showSetupWindow();

    app.on("activate", () => showMainWindow());
  });

  app.on("before-quit", () => {
    isQuitting = true;
  });

  // Tray-resident: keep running when all windows are closed/hidden.
  app.on("window-all-closed", () => {});
}
