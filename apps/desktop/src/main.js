"use strict";

const path = require("node:path");
const { app, BrowserWindow, Tray, Menu, Notification, desktopCapturer, ipcMain, shell, nativeImage } = require("electron");
const Store = require("electron-store");
const { normalizeServerUrl } = require("./url");
const { trayRecorderItems, trayTooltip, notificationFor } = require("./recorderState");

// In dev we load the Vite dev server directly and skip first-run setup.
const DEV_URL = process.env.DIARIZ_DEV ? "http://localhost:5173" : null;

const store = new Store({ name: "diariz-config" });
const ICON = nativeImage.createFromPath(path.join(__dirname, "..", "build", "icon.png"));

let tray = null;
let mainWindow = null;
let setupWindow = null;
let isQuitting = false;

// Tray-driven recording state. `ready` flips true once the web app's recorder has
// mounted (i.e. the user is loaded and signed in) and can be driven from the tray.
let recorder = { phase: "idle", source: null, ready: false };
let recordingStartedAt = 0;
let recordingTicker = null;

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
    setRecorderReady(false);
  });

  // The recorder lives in the web app; until it (re)mounts and reports in, the tray
  // can't drive it. Any fresh navigation/reload drops readiness until it reports again.
  mainWindow.webContents.on("did-start-loading", () => setRecorderReady(false));

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

// ---- Tray-driven recording ----

// Tell the renderer to start/stop. Recording happens in the web app's MediaRecorder
// and keeps running while the window is hidden, so we don't reveal it (background
// recording). If nothing is ready to drive, open the app so the user can sign in.
function startRecording(source) {
  if (!recorder.ready || !mainWindow) {
    showMainWindow();
    return;
  }
  mainWindow.webContents.send("tray:command", { type: "start", source });
}

function stopRecording() {
  if (mainWindow) mainWindow.webContents.send("tray:command", { type: "stop" });
}

// Apply a phase report from the renderer: raise a notification on meaningful
// transitions, then refresh the tray. An "error" report settles back to idle.
function applyRecorderState(next) {
  const prev = recorder;
  const note = notificationFor(prev, next);

  if (next.phase === "recording" && prev.phase !== "recording") recordingStartedAt = Date.now();
  recorder = { ...recorder, phase: next.phase, source: next.source ?? null };
  // The renderer's mount ping carries ready:true; active phases imply readiness too.
  if (typeof next.ready === "boolean") recorder.ready = next.ready;
  else if (next.phase === "recording" || next.phase === "uploading") recorder.ready = true;

  if (note && Notification.isSupported()) new Notification(note).show();

  // A 1s ticker keeps the "Stop Recording (mm:ss)" label live while recording.
  if (recorder.phase === "recording" && !recordingTicker) {
    recordingTicker = setInterval(refreshTray, 1000);
  } else if (recorder.phase !== "recording" && recordingTicker) {
    clearInterval(recordingTicker);
    recordingTicker = null;
  }

  if (next.phase === "error") recorder.phase = "idle";
  refreshTray();
}

function setRecorderReady(ready) {
  if (recorder.ready === ready) return;
  recorder.ready = ready;
  refreshTray();
}

// ---- Tray ----

function refreshTray() {
  if (!tray) return;
  const elapsedMs = recorder.phase === "recording" ? Date.now() - recordingStartedAt : 0;
  const recordItems = trayRecorderItems(recorder, elapsedMs).map((item) => ({
    label: item.label,
    enabled: item.enabled,
    click: () => {
      if (item.id === "record-mic") startRecording("mic");
      else if (item.id === "record-system") startRecording("system");
      else if (item.id === "stop") stopRecording();
    },
  }));

  tray.setToolTip(trayTooltip(recorder));
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: "Open Diariz", click: () => showMainWindow() },
      { type: "separator" },
      ...recordItems,
      { type: "separator" },
      { label: "Settings…", click: () => showSetupWindow() },
      { label: "Quit", click: () => { isQuitting = true; app.quit(); } },
    ]),
  );
}

function buildTray() {
  const trayIcon = ICON.isEmpty() ? ICON : ICON.resize({ width: 16, height: 16 });
  tray = new Tray(trayIcon);
  tray.on("click", () => showMainWindow());
  refreshTray();
}

ipcMain.on("recorder:state", (_event, state) => {
  if (state && typeof state.phase === "string") applyRecorderState(state);
});

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
