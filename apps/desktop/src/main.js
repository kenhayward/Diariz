"use strict";

const path = require("node:path");
const os = require("node:os");
const crypto = require("node:crypto");
const { randomUUID } = require("node:crypto");
const {
  app,
  BrowserWindow,
  Tray,
  Menu,
  Notification,
  desktopCapturer,
  ipcMain,
  shell,
  nativeImage,
  screen,
  globalShortcut,
  dialog,
} = require("electron");
const Store = require("electron-store");
const { normalizeServerUrl, opensExternally } = require("./url");
const { trayRecorderItems, trayTooltip, notificationFor, quitConfirmation } = require("./recorderState");
const { updateRestartItem, notificationForUpdate, isNewerVersion } = require("./updateState");
const { notificationForDownload } = require("./downloadState");
const { documentLoadOptions, trayReloadItem } = require("./documentLoad");
const { buildStartUrl, codeFromArgv, notificationForAuthError } = require("./desktopAuth");
const { cropRectFor, resizeDims, clampRect, sourceForDisplay } = require("./captureTarget");
const { reconcilePool } = require("./pickerPool");
const { RENDERER_INVALIDATING_EVENTS } = require("./rendererReadiness");
const { notesWindowBounds } = require("./notesWindowState");
const { contextMenuItems } = require("./contextMenu");
const {
  SYNC_DEFAULTS,
  windowForScope,
  normalizeAppointment,
  dedupeUids,
  capEvents,
  shouldStartSync,
  isStickyUnavailable,
  trayOutlookItems,
  notificationForSyncResult,
} = require("./outlookSync");
const outlookHost = require("./outlookHost");
const { deviceIdFor } = require("./deviceId");
const {
  trayScreenshotItems,
  DEFAULT_ACCELERATOR,
  normalizeAccelerator,
  isValidAccelerator,
  canCapture,
  shouldStartCapture,
  notificationForCaptureFailure,
  notificationForHotkeyUnavailable,
  acceleratorFromKeyDescriptor,
  unsupportedKeyCaptureMessage,
  hotkeyUnavailableSaveError,
} = require("./screenshotState");

// In dev we load the Vite dev server directly and skip first-run setup.
const DEV_URL = process.env.DIARIZ_DEV ? "http://localhost:5173" : null;

const store = new Store({ name: "diariz-config" });
const ICON = nativeImage.createFromPath(path.join(__dirname, "..", "build", "icon.png"));

let tray = null;
let mainWindow = null;
let setupWindow = null;
let hotkeyWindow = null;
let notesWindow = null;
let isQuitting = false;

// Tray-driven recording state. `ready` flips true once the web app's recorder has
// mounted (i.e. the user is loaded and signed in) and can be driven from the tray.
let recorder = { phase: "idle", source: null, ready: false };
let recordingStartedAt = 0;
let recordingTicker = null;

// Auto-update state. `autoUpdater` is lazily required (packaged builds only).
let autoUpdater = null;
let update = { ready: false, version: null };
let pendingManualCheck = false;

let pendingVerifier = null;

/// The origin the web app is loaded from (dev server, or the configured server).
function targetUrl() {
  return DEV_URL || store.get("serverUrl") || null;
}

// ---- Right-click context menu ----

/// Give a window the right-click menu Electron does not ship: spelling suggestions for the word under
/// the cursor, "Add to dictionary", and the edit roles. Chromium's spellchecker is on by default (hence
/// the red underlines), but its context menu belongs to Chrome's browser UI rather than the content
/// layer Electron embeds, so without this a right-click on an underlined word does nothing at all
/// (issue #678). On Windows this is also the only cut/copy/paste in the app, which runs menu-less.
///
/// `contextMenuItems` decides the shape; this only maps its descriptors onto Electron calls. An empty
/// array means pop nothing - see that module for why.
function attachContextMenu(win) {
  win.webContents.on("context-menu", (_e, params) => {
    const items = contextMenuItems(params);
    if (items.length === 0) return;

    const template = items.map((item) => {
      switch (item.action) {
        case "replace-misspelling":
          return { label: item.label, click: () => win.webContents.replaceMisspelling(item.replacement) };
        case "add-to-dictionary":
          return {
            label: item.label,
            click: () => win.webContents.session.addWordToSpellCheckerDictionary(item.word),
          };
        default:
          return item;
      }
    });

    // The window may have gone between the right-click and this popup (a reload, a close-to-tray)
    // - popup() on a destroyed window throws.
    if (win.isDestroyed()) return;
    Menu.buildFromTemplate(template).popup({ window: win });
  });
}

/// Window options for a same-origin popup - a help article opened from a `?` popover, say. Mirrors the
/// main window's hardening exactly (contextIsolation on, sandbox on, nodeIntegration off) and carries the
/// same preload, so the SPA still sees `window.diariz` and behaves as the desktop app rather than
/// believing it is in a browser tab. Same session as its opener, which is the point: the login comes with
/// it.
function popupWindowOptions() {
  return {
    icon: ICON,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  };
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

  // One handler, two callers, told apart by whether audio was asked for.
  //
  //   - System-audio recording asks for BOTH. On Windows `audio: 'loopback'` records what the system is
  //     playing; a screen video source must be supplied even though the renderer discards the video track,
  //     so which screen it is does not matter.
  //   - Auto-capture asks for video ONLY, and which screen it is matters entirely: it must be the display
  //     the user picked. Answering that request with `sources[0]` would silently auto-capture the wrong
  //     monitor - a stream of perfectly good screenshots of somewhere else.
  //
  // Both grant without a picker: the user has already chosen, either by starting a system-audio recording
  // or by picking a capture area.
  mainWindow.webContents.session.setDisplayMediaRequestHandler(
    (request, callback) => {
      const wantsScreenOnly = request.audioRequested === false;
      desktopCapturer
        .getSources({ types: ["screen"] })
        .then((sources) => {
          if (!wantsScreenOnly) return callback({ video: sources[0], audio: "loopback" });
          const source = captureTarget ? sourceForDisplay(sources, captureTarget.displayId) : null;
          // No target, or its display has gone: deny rather than hand over a different screen.
          callback(source ? { video: source } : {});
        })
        .catch(() => callback({}));
    },
    { useSystemPicker: false },
  );

  // The shell replaced the browser, and with it the download shelf: without this, a download has no
  // progress, no completion notice and no visible failure - a multi-GB platform backup just goes quiet.
  // Deliberately does NOT call item.setSavePath, so Electron's Save-As dialog stays: a backup carries every
  // password hash on the platform and should land where the admin chose.
  //
  // Raw byte counts only. The renderer owns the arithmetic and the wording, where formatBytes and the
  // locale catalogs already live.
  let downloadSeq = 0;
  mainWindow.webContents.session.on("will-download", (_event, item) => {
    const id = ++downloadSeq;
    // Not `url`: that name belongs to the window's start address in the enclosing scope.
    const downloadUrl = item.getURL();
    const filename = item.getFilename();
    const startedAt = Date.now();
    const send = (payload) => {
      if (!mainWindow || mainWindow.isDestroyed()) return;
      mainWindow.webContents.send("download:event", { id, url: downloadUrl, filename, ...payload });
    };

    send({ type: "started", totalBytes: item.getTotalBytes(), receivedBytes: 0 });
    item.on("updated", () => {
      send({
        type: "progress",
        totalBytes: item.getTotalBytes(),
        receivedBytes: item.getReceivedBytes(),
      });
    });
    item.once("done", (_doneEvent, state) => {
      send({
        type: "done",
        state,
        savePath: item.getSavePath(),
        totalBytes: item.getTotalBytes(),
        receivedBytes: item.getReceivedBytes(),
      });
      const note = notificationForDownload(state, { filename, elapsedMs: Date.now() - startedAt });
      if (note && Notification.isSupported()) new Notification(note).show();
    });
  });

  const origin = new URL(url).origin;
  // Open external links in the system browser; keep navigation within the server origin.
  mainWindow.webContents.setWindowOpenHandler(({ url: target }) => {
    if (opensExternally(target, origin)) {
      shell.openExternal(target);
      return { action: "deny" };
    }
    // Same origin, so open it OURSELVES rather than handing it to the browser. This used to externalise
    // everything, which is why an in-app link opened in a new tab landed on a sign-in page: the system
    // browser has none of this app's session. `will-navigate` immediately below always got this right;
    // the two are now consistent.
    return { action: "allow", overrideBrowserWindowOptions: popupWindowOptions() };
  });
  mainWindow.webContents.on("will-navigate", (e, target) => {
    if (new URL(target).origin !== origin) {
      e.preventDefault();
      shell.openExternal(target);
    }
  });

  attachContextMenu(mainWindow);

  // Close to tray/menu bar rather than quitting (this is a tray-resident app).
  mainWindow.on("close", (e) => {
    if (!isQuitting) {
      e.preventDefault();
      mainWindow.hide();
    }
  });
  mainWindow.on("closed", () => {
    mainWindow = null;
    // A pop-out outliving the window that feeds it can only sit there dead - it has no other source
    // of notes, and no way to deliver what is typed into it.
    closeNotesPopout();
    setRecorderReady(false);
  });

  // The recorder lives in the web app; until it (re)mounts and reports in, the tray
  // can't drive it. Only a replaced document (or a dead renderer) unmounts it - see
  // rendererReadiness.js for why the loading state is emphatically not that signal.
  for (const event of RENDERER_INVALIDATING_EVENTS) {
    mainWindow.webContents.on(event, () => setRecorderReady(false));
  }

  // Never from cache without asking - see documentLoad.js. The shell loads someone else's SPA, so a stale
  // document means running an old build of the whole app with nothing on screen to say so.
  mainWindow.loadURL(url, documentLoadOptions());
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

/// Throw away whatever the window is holding and fetch the app again, ignoring the cache entirely - the
/// escape hatch for a shell that has ended up on an old build. Stronger than the `pragma: no-cache` used on a
/// normal load, which only revalidates: this reloads the subresources too, so a client whose cached copy of
/// the document was itself stored under the wrong headers still comes back current.
///
/// Brings the window up first, since the usual reason to reach for this is that what is on screen looks wrong
/// - and with no window there is nothing to reload, so `showMainWindow` (which opens setup when no server is
/// configured) is the whole of the correct behaviour.
function reloadMainWindow() {
  const existed = Boolean(mainWindow && !mainWindow.isDestroyed());
  showMainWindow();
  // Only a window that was already up. One we just created is mid-fetch of a fresh document already, and
  // reloading it here would cancel that load to start the same one again.
  if (existed && mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.reloadIgnoringCache();
}

// ---- Pop-out live-notes window ----
//
// A second window on the SAME origin as the main window, which is what lets the two halves of the
// notes UI talk over a BroadcastChannel with no IPC of their own. The main window stays the owner of
// the recorder, the note lines and the capture stash; this one is a remote control.
//
// Always-on-top at Electron's DEFAULT level. A spike confirmed that survives another application going
// full screen, so the higher "screen-saver" band buys nothing here - and it would also float the notes
// over the lock screen, which they have no business doing.

function showNotesPopout() {
  const url = targetUrl();
  if (!url) return { ok: false };

  if (notesWindow && !notesWindow.isDestroyed()) {
    notesWindow.show();
    notesWindow.focus();
    return { ok: true };
  }

  const bounds = notesWindowBounds(store.get("notesPopoutBounds"), screen.getAllDisplays());
  notesWindow = new BrowserWindow({
    ...bounds,
    alwaysOnTop: true,
    title: "Diariz - Notes",
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "notes-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  notesWindow.setMenuBarVisibility(false);

  const origin = new URL(url).origin;
  notesWindow.webContents.setWindowOpenHandler(({ url: target }) => {
    if (opensExternally(target, origin)) {
      shell.openExternal(target);
      return { action: "deny" };
    }
    // Same origin, so open it OURSELVES rather than handing it to the browser. This used to externalise
    // everything, which is why an in-app link opened in a new tab landed on a sign-in page: the system
    // browser has none of this app's session. `will-navigate` immediately below always got this right;
    // the two are now consistent.
    return { action: "allow", overrideBrowserWindowOptions: popupWindowOptions() };
  });
  notesWindow.webContents.on("will-navigate", (e, target) => {
    if (new URL(target).origin !== origin) {
      e.preventDefault();
      shell.openExternal(target);
    }
  });

  attachContextMenu(notesWindow);

  // Bounds are tracked in memory as the window is dragged and resized, then written once when it has
  // gone. Writing the store on every drag frame would be pure churn, and writing it from "close" turned
  // out not to be reliable when the renderer closes itself - "closed" always arrives (it is what drives
  // the notes:closed report below), but by then the window is destroyed and getBounds() is gone, hence
  // the cached copy. The key is flat: every other key in this store is, and dotted keys are unproven here.
  let lastBounds = null;
  const trackBounds = () => {
    if (notesWindow && !notesWindow.isDestroyed()) lastBounds = notesWindow.getBounds();
  };
  notesWindow.on("move", trackBounds);
  notesWindow.on("resize", trackBounds);
  notesWindow.on("close", trackBounds);
  trackBounds(); // seed it, so a window that is never touched still remembers where it sat
  notesWindow.on("closed", () => {
    if (lastBounds) store.set("notesPopoutBounds", lastBounds);
    notesWindow = null;
    // The guaranteed way back to the inline notes popover. The renderer also sends its own "closing"
    // over the channel, but that cannot be relied on if the renderer died rather than closed.
    if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send("notes:closed");
  });

  notesWindow.loadURL(new URL("/notes-popout", url).toString(), documentLoadOptions());
  return { ok: true };
}

function closeNotesPopout() {
  if (notesWindow && !notesWindow.isDestroyed()) notesWindow.close();
}

ipcMain.handle("notes:open", () => showNotesPopout());

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
  attachContextMenu(setupWindow);
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
    if (mainWindow) mainWindow.loadURL(result.origin, documentLoadOptions());
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

  if (next.phase === "recording" && prev.phase !== "recording") {
    recordingStartedAt = Date.now();
    setCaptureTarget(null); // each recording chooses its own capture area
  } else if (prev.phase === "recording" && next.phase !== "recording") {
    // Recording ended (stopped, errored, or the renderer dropped out) while the capture
    // overlay was up - don't strand an always-on-top window over every display; any
    // selection made after this point would be discarded by the post-await phase
    // re-check anyway.
    dismissPickerIfOpen();
  }
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
  applyShortcut();
  syncPickerWarmth();
  syncAutoCapture();
  refreshTray();
}

function setRecorderReady(ready) {
  if (recorder.ready === ready) return;
  recorder.ready = ready;
  // `ready` flipping false (reload, window close) must drop a held shortcut immediately -
  // `recorder.phase` alone goes stale here, so re-evaluate the gate now, not just on the
  // next phase report. The overlay pool follows the same gate.
  applyShortcut();
  syncPickerWarmth();
  syncAutoCapture();
  refreshTray();
}

// ---- Desktop Google sign-in (system browser + diariz:// deep link) ----

// base64url(sha256(verifier)) - matches the API's OAuthPkce.Challenge (ASCII verifier, no padding).
function s256(verifier) {
  return crypto.createHash("sha256").update(verifier, "ascii").digest("base64url");
}

// Renderer asked to start Google sign-in: generate PKCE, open the server's start URL in the SYSTEM
// browser (Google refuses embedded webviews), and keep the verifier to redeem the code later.
function startGoogleSignIn() {
  const server = targetUrl();
  if (!server) return;
  const verifier = crypto.randomBytes(32).toString("base64url");
  pendingVerifier = verifier;
  const origin = new URL(server).origin;
  shell.openExternal(buildStartUrl(origin, s256(verifier)));
}

// A diariz:// deep link arrived (argv on cold start, or the second-instance event). Redeem the code
// for a token and hand it to the renderer; then surface the window.
async function handleAuthDeepLink(argv) {
  const code = codeFromArgv(argv);
  if (!code) return; // not an auth deep link (e.g. a normal launch) - nothing to do or report
  // A code arrived. Any failure from here is surfaced (native notification + auth:error to the renderer)
  // rather than silently leaving the user on the login screen.
  if (!pendingVerifier) return reportAuthError("expired"); // sign-in state lost (app restarted mid-flow?)
  const verifier = pendingVerifier;
  pendingVerifier = null;
  const server = targetUrl();
  if (!server) return reportAuthError("expired");
  try {
    const res = await fetch(`${new URL(server).origin}/api/auth/desktop/exchange`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code, verifier }),
    });
    if (!res.ok) return reportAuthError("rejected");
    const { accessToken } = await res.json();
    if (accessToken) deliverAuthToken(accessToken);
    else reportAuthError("rejected");
  } catch {
    reportAuthError("network"); // couldn't reach the server (offline, DNS, TLS)
  }
}

// Surface a whole window to the renderer, waiting for the page to finish loading on a cold start. Used
// for both the signed-in token and sign-in failures.
// ---- Desktop Outlook calendar ----
//
// The shell can read the local calendar but holds no token; the renderer holds the token but cannot see
// Outlook. So this harvests a window and hands it over for the web app to POST - the same split the screenshot
// feature uses. Nothing runs until the renderer reports the user has opted in.
const outlook = {
  phase: "idle",           // idle | reading | pushing
  available: false,        // Windows, the reader is present, AND classic Outlook is installed
  enabled: false,          // the user's opt-in, as reported by the renderer
  inFlight: false,
  lastSyncAt: 0,           // last full run
  lastQuickSyncAt: 0,      // last today-only run - its own stamp, see shouldStartSync
  lastError: null,
  cfg: { ...SYNC_DEFAULTS },
  launchSyncDone: false,
};

/// Store key holding "there is no classic Outlook on this PC" (the reader's reason), so nothing probes or
/// activates again until the user asks from Preferences. See isStickyUnavailable for which reasons qualify.
const OUTLOOK_UNAVAILABLE_KEY = "outlookUnavailableReason";

/// Decide whether this machine can reach Outlook, and remember a permanent "no".
///
/// The remembering is the point. A PC with Office but no classic Outlook used to get a Windows *install
/// Outlook* prompt on every launch, because the launch sync activated the COM class to find out. The reader
/// now answers from the registry, and a definitive "not installed" is written down so even that probe stops
/// running - `recheckOutlook()` (Preferences) is the only thing that clears it.
async function resolveOutlookAvailability({ force = false } = {}) {
  if (!outlookHost.isAvailable()) {
    outlook.available = false;
    return outlook.available;
  }

  if (force) store.delete(OUTLOOK_UNAVAILABLE_KEY);
  else if (isStickyUnavailable(store.get(OUTLOOK_UNAVAILABLE_KEY))) {
    outlook.available = false;
    return outlook.available;
  }

  const { ok, reason } = await outlookHost.probe();
  if (!ok && isStickyUnavailable(reason)) store.set(OUTLOOK_UNAVAILABLE_KEY, reason);
  outlook.available = ok;
  outlook.lastError = ok ? null : reason || "unavailable";
  refreshTray();
  return outlook.available;
}

/// The answer, resolved once and shared. Everything that needs to know whether Outlook is reachable goes
/// through this rather than reading `outlook.available` directly: the probe is a subprocess, so the renderer's
/// `outlook:ready` (which licenses the launch sync) routinely arrives while it is still running, and reading
/// the flag then would report "unavailable" on every launch.
function outlookAvailability({ force = false } = {}) {
  if (force || !outlook.availability) outlook.availability = resolveOutlookAvailability({ force });
  return outlook.availability;
}

function setOutlookPhase(phase) {
  if (outlook.phase === phase) return;
  outlook.phase = phase;
  refreshTray();
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("outlook:state", {
      phase,
      lastSyncAt: outlook.lastSyncAt,
      lastError: outlook.lastError,
    });
  }
}

/// Read the calendar and hand the window to the renderer to upload.
///
/// `scope` is `all` (the configured rolling window - the full read, which takes tens of seconds on a busy
/// mailbox) or `today` (local midnight to midnight, the quick sync the Calendar toolbar offers for picking up
/// a meeting that has just appeared). Only the window differs; everything downstream, including the server's
/// window-scoped sweep, is identical.
///
/// Resolves to a reason when it did not start, so a caller (the tray, or the button in the web app) can say
/// why rather than appearing to do nothing.
async function syncOutlook({ scope = "all", date = undefined } = {}) {
  if (process.platform !== "win32") return { started: false, reason: "not-windows" };
  if (!(await outlookAvailability())) return { started: false, reason: "unavailable" };
  if (!outlook.enabled) return { started: false, reason: "disabled" };
  if (outlook.inFlight) return { started: false, reason: "busy" };
  if (!shouldStartSync(outlook, Date.now(), scope)) return { started: false, reason: "cooldown" };

  outlook.inFlight = true;
  setOutlookPhase("reading");
  try {
    // `date` is the day the user has selected in the calendar, for the quick sync. Absent (an older web
    // build, or nothing selected) falls back to today inside windowForScope, which is what it always read.
    const { start, end } = windowForScope(new Date(), outlook.cfg, scope, date);
    const result = await outlookHost.read({
      start,
      end,
      skipPrivate: outlook.cfg.skipPrivate,
      includeBody: outlook.cfg.includeBody,
    });

    if (!result.ok) {
      outlook.lastError = result.reason || "error";
      // A failure that means "there is no classic Outlook here" is written down and the connector switched
      // off, so the next launch does not try again - that repetition is what made the install prompt a
      // recurring annoyance rather than a one-off.
      if (isStickyUnavailable(result.reason)) {
        store.set(OUTLOOK_UNAVAILABLE_KEY, result.reason);
        outlook.available = false;
        refreshTray();
      }
      const note = notificationForSyncResult({ ok: false, reason: result.reason });
      if (note && Notification.isSupported()) new Notification(note).show();
      return { started: false, reason: result.reason || "error" };
    }

    const events = capEvents(
      dedupeUids(
        (result.events || [])
          .map((raw) => normalizeAppointment(raw, outlook.cfg))
          .filter(Boolean),
      ),
    );

    setOutlookPhase("pushing");
    // The renderer POSTs from here; `outlook:result` reports what the server did.
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("outlook:push", {
        syncId: randomUUID(),
        device: {
          deviceId: deviceIdFor(store),
          deviceName: os.hostname(),
          mailboxName: result.mailboxName || null,
          timeZone: result.deviceTimeZone || null,
        },
        windowStart: start,
        windowEnd: end,
        // Passed through untouched: a partial read must reach the server as incomplete, or it would treat the
        // events the failed read never got to as cancellations and delete them.
        complete: result.complete !== false,
        events,
      });
    }
    return { started: true };
  } catch (err) {
    outlook.lastError = "error";
    console.error("Outlook sync failed:", err);
    return { started: false, reason: "error" };
  } finally {
    outlook.inFlight = false;
    // Stamped per scope: a quick run must not start the full run's minute-long cooldown, and vice versa.
    if (scope === "today") {
      outlook.lastQuickSyncAt = Date.now();
    } else {
      outlook.lastSyncAt = Date.now();
      store.set("outlookLastSyncAt", outlook.lastSyncAt);
    }
    if (outlook.phase !== "pushing") setOutlookPhase("idle");
  }
}

// The renderer telling us the connector's settings - and, by arriving at all, that a signed-in renderer is
// ready to POST. That is what licenses the launch sync: app-ready cannot be the trigger, because the user may
// not be signed in yet and the push needs their token.
ipcMain.on("outlook:ready", async (_e, cfg) => {
  outlook.enabled = cfg?.enabled === true;
  outlook.cfg = {
    pastDays: cfg?.pastDays ?? SYNC_DEFAULTS.pastDays,
    futureDays: cfg?.futureDays ?? SYNC_DEFAULTS.futureDays,
    skipPrivate: cfg?.skipPrivate !== false,
    includeBody: cfg?.includeBody !== false,
  };
  refreshTray();

  if (outlook.enabled && !outlook.launchSyncDone && (await outlookAvailability())) {
    outlook.launchSyncDone = true;
    void syncOutlook();
  }
});

ipcMain.handle("outlook:available", () => outlookAvailability());
// Where a sync has got to *right now*. `outlook:state` is otherwise push-only, and pushes only changes - so a
// renderer that subscribed after a run started (every launch sync, and every reload mid-sync) had no way to
// find out one was under way, and left its calendar sync buttons live through the whole of it.
ipcMain.handle("outlook:state", () => ({
  phase: outlook.phase,
  lastSyncAt: outlook.lastSyncAt,
  lastError: outlook.lastError,
}));
ipcMain.handle("outlook:sync-now", (_e, options) => syncOutlook(options || {}));
// Preferences asking us to look again: the only thing that clears a remembered "no classic Outlook here".
ipcMain.handle("outlook:recheck", () => outlookAvailability({ force: true }));

ipcMain.on("outlook:result", (_e, result) => {
  outlook.lastError = result?.ok ? null : result?.error || "error";
  setOutlookPhase("idle");
  const note = notificationForSyncResult(result);
  if (note && Notification.isSupported()) new Notification(note).show();
});

function sendToRenderer(channel, payload) {
  showMainWindow();
  if (!mainWindow) return;
  const wc = mainWindow.webContents;
  if (wc.isLoading()) wc.once("did-finish-load", () => wc.send(channel, payload));
  else wc.send(channel, payload);
  mainWindow.show();
  mainWindow.focus();
}

function deliverAuthToken(token) {
  sendToRenderer("auth:token", token);
}

// A desktop sign-in failed: pop a native notification and tell the renderer so the login screen can show
// why, instead of the old silent return that left the user staring at the login form.
function reportAuthError(reason) {
  const note = notificationForAuthError(reason);
  if (note && Notification.isSupported()) new Notification(note).show();
  sendToRenderer("auth:error", reason);
}

// ---- Auto-update (packaged builds only) ----

function notifyUpdate(kind, opts) {
  const note = notificationForUpdate(kind, opts);
  if (!note || !Notification.isSupported()) return;
  const n = new Notification(note);
  if (kind === "downloaded") n.on("click", restartToUpdate);
  n.show();
}

function restartToUpdate() {
  if (!autoUpdater) return;
  // Confirmed BEFORE `isQuitting` is set: installing an update restarts the app, which ends a recording just
  // as surely as quitting does. Setting the flag first and then being cancelled would leave the window
  // closing for real instead of hiding to the tray.
  if (!confirmQuit()) return;
  isQuitting = true;
  autoUpdater.quitAndInstall();
}

function checkForUpdates(manual) {
  // macOS (unsigned POC): Squirrel.Mac can't auto-update an unsigned app and there is no mac feed, so use a
  // lightweight GitHub-Releases check that opens the download page when a newer tag exists (Milestone B
  // swaps this for electron-updater once the build is signed).
  if (process.platform === "darwin") {
    void checkForUpdatesMac(manual);
    return;
  }
  if (!autoUpdater) {
    if (manual) notifyUpdate("not-available", { manual: true, version: app.getVersion() });
    return;
  }
  pendingManualCheck = manual;
  autoUpdater.checkForUpdates().catch(() => {
    notifyUpdate("error", { manual });
    pendingManualCheck = false;
  });
}

/// owner/repo parsed from package.json's repository URL (fork-friendly), or null.
function githubRepo() {
  try {
    const url = require("../package.json").repository?.url || "";
    const m = url.match(/github\.com[/:]([^/]+)\/([^/.]+)/i);
    return m ? `${m[1]}/${m[2]}` : null;
  } catch {
    return null;
  }
}

/// macOS manual update check: compare the app version against the latest GitHub release tag; if newer,
/// notify and (on a manual check) open the Releases page. Automatic checks only notify, never auto-open.
async function checkForUpdatesMac(manual) {
  const repo = githubRepo();
  if (!repo) return;
  try {
    const res = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
      headers: { Accept: "application/vnd.github+json", "User-Agent": "Diariz" },
    });
    if (!res.ok) throw new Error(`GitHub API ${res.status}`);
    const data = await res.json();
    if (isNewerVersion(app.getVersion(), data.tag_name)) {
      if (Notification.isSupported()) {
        new Notification({ title: "Diariz", body: `A new version (${data.tag_name}) is available` }).show();
      }
      if (manual) shell.openExternal(data.html_url || `https://github.com/${repo}/releases`);
    } else if (manual) {
      notifyUpdate("not-available", { manual: true, version: app.getVersion() });
    }
  } catch {
    if (manual) notifyUpdate("error", { manual: true });
  }
}

function setupAutoUpdater() {
  // electron-updater only works in a packaged, signed build (Squirrel.Mac refuses unsigned; it also reads
  // app-update.yml). On the unsigned macOS POC use the manual GitHub check instead of electron-updater.
  if (!app.isPackaged) return;
  if (process.platform === "darwin") {
    void checkForUpdatesMac(false);
    setInterval(() => void checkForUpdatesMac(false), 6 * 60 * 60 * 1000);
    return;
  }
  autoUpdater = require("electron-updater").autoUpdater;
  autoUpdater.autoDownload = true; // fetch in the background
  autoUpdater.autoInstallOnAppQuit = true; // also apply on a normal quit

  autoUpdater.on("update-available", (info) =>
    notifyUpdate("available", { version: info?.version, manual: pendingManualCheck }),
  );
  autoUpdater.on("update-not-available", () => {
    notifyUpdate("not-available", { manual: pendingManualCheck, version: app.getVersion() });
    pendingManualCheck = false;
  });
  autoUpdater.on("error", () => {
    notifyUpdate("error", { manual: pendingManualCheck });
    pendingManualCheck = false;
  });
  autoUpdater.on("update-downloaded", (info) => {
    update = { ready: true, version: info?.version || null };
    refreshTray();
    notifyUpdate("downloaded", { version: info?.version });
    pendingManualCheck = false;
  });

  checkForUpdates(false);
  setInterval(() => checkForUpdates(false), 6 * 60 * 60 * 1000); // every 6 hours
}

// ---- Launch at login ----

function openAtLogin() {
  return app.getLoginItemSettings().openAtLogin;
}

function toggleOpenAtLogin() {
  app.setLoginItemSettings({ openAtLogin: !openAtLogin() });
  refreshTray();
}

// ---- Screenshot capture ----

const MAX_LONG_EDGE = 2560;
const THUMB_LONG_EDGE = 320;

// The capture area chosen for the CURRENT recording: { displayId, selection } or null.
// Cleared on every transition into "recording" so each meeting picks fresh (a stale
// rectangle from a previous monitor layout would silently capture the wrong thing).
let captureTarget = null;
// The picker overlays, keyed by display id. These are PRE-WARMED while a recording is
// running (syncPickerWarmth) and reused for every pick, because building them on demand
// measured at 400-750ms on a three-display machine - half a second in which nothing is on
// screen and the app window still takes input, so the button reads as dead and an
// impatient second click gets eaten by the overlay the moment it lands. See pickerPool.js.
let pickerWindows = new Map();
// Display ids whose overlay has painted at least once, so `show()` puts something visible
// on screen rather than an empty transparent window. A cold pool (warm-up never ran, or a
// display appeared just now) waits for `ready-to-show` instead.
let pickerReady = new Set();
let pickerResolve = null;
// The in-flight picker promise, if any. Guards against re-entrancy: a held-down global
// hotkey auto-repeats, and the tray click is also reachable while a picker is already
// showing, so a second `openPicker()` call must reuse the first invocation's promise
// rather than destroying its windows and orphaning it forever.
let pickerPromise = null;

// Capture re-entrancy/rate-limit bookkeeping, consulted by the pure `shouldStartCapture`
// predicate in screenshotState.js. `inFlight` covers the entire pick-grab-send sequence
// (not just the grab), so a held hotkey can never start a second capture while the first
// is still choosing an area or encoding an image; `lastCaptureAt` bounds how soon a *new*
// capture can start after the last one finished.
let captureInFlight = false;
let lastCaptureAt = 0;

// Whether auto-capture is running for the current recording. The LOOP itself lives in the renderer (see
// apps/web/src/lib/slideCapture.ts): sampling a warm getDisplayMedia stream costs ~12ms against ~430ms
// for a desktopCapturer grab, and only the renderer knows the pause-aware recording clock. The shell
// owns which display is captured, the on/off state, and the tray item.
let autoCapture = false;

/// Every write to `captureTarget` goes through here so the renderer can mirror the state: the web app
/// disables its capture button until an area exists (capturing without one opens the picker and leaves the
/// buttons inert until it settles, which reads as a frozen popover). Assigning the variable directly would
/// leave the button stale, so there is deliberately no other assignment site.
function setCaptureTarget(next) {
  captureTarget = next;
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("screenshot:area-changed", captureTarget !== null);
  }
  // A changed area invalidates the stream the renderer is holding - it was granted for the old display
  // and cropped for the old rectangle. Stop rather than quietly capture the wrong thing; the user just
  // told us what they want to capture, so this is also the moment they are looking.
  if (autoCapture) setAutoCapture(false);
}

/// What the renderer needs to open its own stream: the target display's physical size, and the chosen
/// rectangle within it (null for a whole screen). Physical pixels, because that is what `cropRectFor`
/// produces and what the stream is requested at.
function autoCaptureArea() {
  if (!captureTarget) return null;
  const display = screen.getAllDisplays().find((d) => d.id === captureTarget.displayId);
  if (!display) return null;
  const scale = display.scaleFactor || 1;
  return {
    displayWidth: Math.round(display.bounds.width * scale),
    displayHeight: Math.round(display.bounds.height * scale),
    crop: cropRectFor(display, captureTarget.selection),
  };
}

/// Every write to `autoCapture` goes through here, for the same reason `setCaptureTarget` exists: the
/// renderer and the tray both mirror this, and a direct assignment would leave one of them lying about
/// whether the screen is being captured.
function setAutoCapture(next) {
  const area = next ? autoCaptureArea() : null;
  // Asked to start with no usable area (the display was unplugged between picking and starting): stay off
  // rather than start a loop with nothing to point at.
  autoCapture = next && area !== null;
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send(
      "screenshot:auto-capture-changed",
      autoCapture ? { active: true, area } : { active: false },
    );
  }
  refreshTray();
}

/// Toggle auto-capture, picking a capture area first if this recording has not chosen one - the same
/// pick-then-act flow the tray's manual capture item has always had. A tray menu cannot explain a greyed
/// item, so the item stays enabled and this does the explaining by simply working.
async function toggleAutoCapture() {
  if (autoCapture) return setAutoCapture(false);
  if (!canCapture(recorder)) return;

  if (!captureTarget) {
    try {
      setCaptureTarget(await openPicker());
    } catch {
      notifyCaptureFailed("error");
      return;
    }
    // Cancelled, or the recording ended while the picker was open.
    if (!captureTarget || !canCapture(recorder)) return;
  }
  setAutoCapture(true);
}

function livePickers() {
  return [...pickerWindows.values()].filter((win) => !win.isDestroyed());
}

/// One hidden, pre-loaded overlay for `display`. Never shown here - warming it is the
/// whole point, so the pick itself is only a show().
function createPickerWindow(display) {
  const win = new BrowserWindow({
    x: display.bounds.x,
    y: display.bounds.y,
    width: display.bounds.width,
    height: display.bounds.height,
    frame: false,
    transparent: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: false,
    movable: false,
    fullscreenable: false,
    focusable: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, "picker-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  win.once("ready-to-show", () => pickerReady.add(display.id));
  // Self-heal: if this overlay never manages to show anything (picker.html fails to
  // load), drop it rather than leaving a dead window around forever. The next reconcile
  // builds a fresh one.
  win.webContents.on("did-fail-load", () => {
    if (!win.isDestroyed()) win.destroy();
  });
  win.on("closed", () => {
    pickerReady.delete(display.id);
    if (pickerWindows.get(display.id) === win) pickerWindows.delete(display.id);
    // A pick in flight with no overlay left to make it can never settle on its own, and
    // a pending picker wedges every later capture attempt - resolve it as cancelled.
    if (pickerResolve && livePickers().length === 0) settlePicker(null);
  });
  win.setAlwaysOnTop(true, "screen-saver");
  win.loadFile(path.join(__dirname, "picker.html"));
  return win;
}

/// Bring the pool in line with the displays that exist right now: build overlays for new
/// displays, drop the ones whose display has gone, and re-fit the survivors (a resolution
/// or arrangement change moves a display's bounds without changing its id). Idempotent and
/// cheap when nothing changed - a kept overlay holds on to its already-painted renderer,
/// which is what makes the next pick instant.
function reconcilePickers() {
  const displays = screen.getAllDisplays();
  const { create, destroy, keep } = reconcilePool([...pickerWindows.keys()], displays.map((d) => d.id));
  for (const id of destroy) {
    const win = pickerWindows.get(id);
    pickerWindows.delete(id);
    pickerReady.delete(id);
    if (win && !win.isDestroyed()) win.destroy();
  }
  for (const id of keep) {
    const win = pickerWindows.get(id);
    const display = displays.find((d) => d.id === id);
    if (win && !win.isDestroyed() && display) win.setBounds(display.bounds);
  }
  for (const id of create) {
    const display = displays.find((d) => d.id === id);
    if (display) pickerWindows.set(id, createPickerWindow(display));
  }
}

/// Build the overlays ahead of the first pick. Failure is not fatal: `openPicker`
/// reconciles again, so a failed warm-up costs latency, not the feature.
function warmPickers() {
  try {
    reconcilePickers();
  } catch {
    // e.g. screen.getAllDisplays() during a display transition - retried on the next open
  }
}

/// Put every overlay away and re-arm it for the next pick. Replaces destroying them: the
/// painted renderers are exactly what we are keeping.
function hidePickers() {
  for (const win of pickerWindows.values()) {
    if (win.isDestroyed()) continue;
    win.hide();
    win.webContents.send("picker:reset");
  }
}

/// Drop the pool entirely - capture is over, so three idle renderers should not outlive it.
function teardownPickers() {
  dismissPickerIfOpen();
  for (const win of pickerWindows.values()) if (!win.isDestroyed()) win.destroy();
  pickerWindows = new Map();
  pickerReady = new Set();
}

/// The overlays exist exactly while a capture could be asked for. Driven off the same
/// `canCapture` gate as the hotkey and the tray items so they can't disagree.
function syncPickerWarmth() {
  if (canCapture(recorder)) warmPickers();
  else teardownPickers();
}

/// Auto-capture cannot outlive the gate that permits capturing at all. A recording that ends or a
/// renderer that reloads takes the loop with it either way; without this the flag would survive, leaving
/// the tray checkbox ticked over a loop that no longer exists. Driven off the same `canCapture` gate as
/// everything else here, and off the capture area still having a display to point at.
function syncAutoCapture() {
  if (!autoCapture) return;
  if (!canCapture(recorder) || autoCaptureArea() === null) setAutoCapture(false);
}

/// Show `win` over its display. The cursor's display is shown focused because the
/// overlay's only cancel path is an Escape keydown handler inside its own window, so one
/// of them MUST hold OS keyboard focus; the rest come up inactive so they can't steal it.
function showPicker(win, displayId, cursorDisplayId) {
  if (win.isDestroyed()) return;
  win.setAlwaysOnTop(true, "screen-saver");
  if (displayId === cursorDisplayId) {
    win.show();
    win.focus();
  } else {
    win.showInactive();
  }
}

/// Show the overlay on every display and resolve with the chosen target
/// ({ displayId, selection }) or null if the user cancelled. If a picker is already
/// showing, returns its existing promise instead of starting a second one.
function openPicker() {
  if (pickerPromise) {
    // A picker is already waiting on a choice. Re-surface it rather than silently handing back the pending
    // promise: from the app the click looked like nothing happened, and if the overlay had slipped behind
    // another window there was no way left to reach it - both capture buttons appeared dead until the
    // recording ended.
    const cursorDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
    for (const [id, win] of pickerWindows) showPicker(win, id, cursorDisplay.id);
    return pickerPromise;
  }
  const attempt = new Promise((resolve, reject) => {
    try {
      pickerResolve = resolve;
      reconcilePickers(); // also builds the pool when warm-up never ran
      const cursorDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
      for (const [id, win] of pickerWindows) {
        if (pickerReady.has(id)) showPicker(win, id, cursorDisplay.id);
        // Cold overlay (the pool was just built): it would show as an empty transparent
        // window, so wait for its first paint. The guard drops a paint that lands after
        // the pick already settled, which would otherwise re-show a put-away overlay.
        else win.once("ready-to-show", () => pickerResolve && showPicker(win, id, cursorDisplay.id));
      }
      if (livePickers().length === 0) throw new Error("no capture-area overlay could be opened");
    } catch (err) {
      reject(err);
    }
  });
  // The executor can settle the pick synchronously - reconciling against a machine that
  // reports no displays destroys the last overlay, and the `closed` handler cancels the
  // pick rather than let it hang. settlePicker has then already cleared the guard, so
  // arming it below would leave a truthy pickerPromise that nothing can ever settle,
  // wedging every later capture attempt. Only arm a pick that is still in flight.
  if (!pickerResolve) return attempt;
  // If the executor threw instead (e.g. screen.getCursorScreenPoint()/new BrowserWindow
  // failing), clear the guard so the NEXT attempt gets a fresh picker instead of reusing
  // a promise that is rejected forever.
  pickerPromise = attempt.catch((err) => {
    pickerPromise = null;
    pickerResolve = null;
    throw err;
  });
  return pickerPromise;
}

function settlePicker(value) {
  const resolve = pickerResolve;
  pickerResolve = null;
  pickerPromise = null;
  hidePickers();
  if (resolve) resolve(value);
}

function dismissPickerIfOpen() {
  if (pickerResolve) settlePicker(null);
}

ipcMain.on("picker:choose", (event, selection) => {
  let displayId = null;
  for (const [id, win] of pickerWindows) if (win.webContents === event.sender) displayId = id;
  if (displayId === null) return settlePicker(null);
  settlePicker({ displayId, selection });
});

ipcMain.on("picker:cancel", () => settlePicker(null));

/// Grab the target display at full resolution, crop to the chosen area, and return
/// { full, thumb, width, height } - or null if the display has gone away.
async function grab(target) {
  const display = screen.getAllDisplays().find((d) => d.id === target.displayId);
  if (!display) return null; // monitor unplugged since the area was chosen

  const scale = display.scaleFactor || 1;
  const sources = await desktopCapturer.getSources({
    types: ["screen"],
    thumbnailSize: {
      width: Math.round(display.bounds.width * scale),
      height: Math.round(display.bounds.height * scale),
    },
  });
  // `display_id` is not contractually populated by desktopCapturer across platforms.
  // Falling back to `sources[0]` would silently grab whatever screen happens to be first
  // and then crop it with the TARGET display's geometry - wrong monitor, no visible
  // error. When nothing matches, return null so the caller clears the target and
  // re-prompts, exactly like the unplugged-display case above.
  const source = sources.find((s) => String(s.display_id) === String(display.id));
  if (!source) return null;

  let image = source.thumbnail;
  const crop = cropRectFor(display, target.selection);
  if (crop) {
    // `thumbnailSize` is a request, not a guarantee - desktopCapturer returns the
    // screen's true pixel size (aspect-fit, never upscaled), which need not equal
    // `bounds.width * scaleFactor` (fractional Windows scaling is the common case).
    // Clamp the crop to what was actually grabbed rather than trusting the assumed size.
    const clamped = clampRect(crop, image.getSize());
    if (clamped.width <= 0 || clamped.height <= 0) return null; // degenerate crop - treat as a failed capture, not an empty image
    image = image.crop(clamped);
  }

  const size = image.getSize();
  const capped = resizeDims(size.width, size.height, MAX_LONG_EDGE);
  const fullImage = capped.width === size.width ? image : image.resize(capped);
  const thumbDims = resizeDims(capped.width, capped.height, THUMB_LONG_EDGE);
  const thumbImage = fullImage.resize(thumbDims);

  return {
    full: fullImage.toPNG(),
    thumb: thumbImage.toJPEG(80),
    width: capped.width,
    height: capped.height,
  };
}

/// Native notification for a failed capture attempt - the established pattern
/// elsewhere in this file (see notifyUpdate/reportAuthError) for surfacing a failure the
/// user can actually see, instead of a silent/unhandled rejection or a silent no-op.
/// Copy lives in screenshotState.js's notificationForCaptureFailure, alongside this
/// shell's other pure notification models.
function notifyCaptureFailed(reason) {
  if (Notification.isSupported()) {
    new Notification(notificationForCaptureFailure(reason)).show();
  }
}

/// Capture now: pick an area first if this recording hasn't chosen one, then grab and
/// push the bytes to the renderer (which owns the recording clock). Guarded against
/// re-entrancy: a held-down global hotkey auto-repeats at roughly 30Hz, so without a
/// guard every repeat would either start its own full-resolution grab (steady state,
/// target already chosen) or pile onto the picker and all fire at once when it settles.
/// `captureInFlight` covers the whole pick-grab-send sequence, and the cooldown after it
/// clears absorbs the auto-repeat tail without swallowing a deliberate second press.
async function captureScreenshot() {
  if (!canCapture(recorder) || !mainWindow) return;
  if (!shouldStartCapture({ inFlight: captureInFlight, lastCaptureAt }, Date.now())) return;
  captureInFlight = true;
  try {
    if (!captureTarget) {
      setCaptureTarget(await openPicker());
      if (!captureTarget) return; // cancelled - no capture, no error
    }
    // The picker await is unbounded (the user may sit on the overlay) and grab() is
    // another await - re-assert the gate right before touching the renderer rather than
    // trusting the check from the top of the function.
    if (!canCapture(recorder) || !mainWindow) return;
    const shot = await grab(captureTarget);
    if (!shot) {
      setCaptureTarget(null); // display gone, or crop degenerated: re-prompt on the next capture
      notifyCaptureFailed("unavailable");
      return;
    }
    if (!canCapture(recorder) || !mainWindow) return;
    mainWindow.webContents.send("screenshot:captured", shot);
  } catch {
    // desktopCapturer rejecting (permission revoked, compositor hiccup, macOS Screen
    // Recording denied), nativeImage.crop on a bad rect, or send() on a torn-down
    // webContents must not become a silent unhandled rejection.
    notifyCaptureFailed("error");
  } finally {
    captureInFlight = false;
    lastCaptureAt = Date.now();
  }
}

async function changeCaptureArea() {
  setCaptureTarget(null);
  if (!canCapture(recorder)) return;
  try {
    setCaptureTarget(await openPicker());
    if (!canCapture(recorder)) setCaptureTarget(null); // recording ended while the picker was open
  } catch {
    notifyCaptureFailed("error");
  }
}

ipcMain.handle("screenshot:capture", () => {
  // A picker is already waiting on a choice, so `captureScreenshot` would bail on its in-flight guard and
  // the click would do nothing visible. Re-surface the overlay instead. Only the UI path does this: the
  // global hotkey auto-repeats at ~30Hz while held, and re-focusing that fast would fight the user's drag.
  if (pickerPromise) return void openPicker();
  return captureScreenshot();
});
ipcMain.handle("screenshot:toggle-auto-capture", () => {
  // A picker is already waiting on a choice, so this would bail and the click would do nothing visible.
  // Re-surface the overlay instead, exactly as screenshot:capture does.
  if (pickerPromise) return void openPicker();
  return toggleAutoCapture();
});
ipcMain.handle("screenshot:change-area", () => changeCaptureArea());
// The renderer's starting value; every later change arrives on "screenshot:area-changed". A renderer that
// reloads mid-recording re-asks rather than assuming no area is set.
ipcMain.handle("screenshot:has-area", () => captureTarget !== null);

// Track whether the user has already been notified that the accelerator couldn't be
// registered, so a failed registration doesn't renotify on every tray refresh - only
// once per recording attempt.
let shortcutWarned = false;

/// The hotkey is registered only while `canCapture` holds - recording AND the renderer's
/// recorder ready to drive - so Diariz never holds a global key while idle, and matches
/// exactly the gate `captureScreenshot`/`trayScreenshotItems` use (`ready` can go
/// stale-false - reload, window close - independently of `phase`, and must drop the
/// shortcut immediately rather than waiting for the next phase report). Returns false
/// when the combination is already taken by other software.
function applyShortcut() {
  globalShortcut.unregisterAll();
  if (!canCapture(recorder)) {
    shortcutWarned = false; // leaving the armed state - the next recording gets a fresh warning
    return true;
  }
  const accelerator = normalizeAccelerator(store.get("captureHotkey")) ?? DEFAULT_ACCELERATOR;
  let ok;
  try {
    ok = globalShortcut.register(accelerator, () => void captureScreenshot());
  } catch {
    ok = false;
  }
  if (ok) {
    shortcutWarned = false;
  } else if (!shortcutWarned) {
    shortcutWarned = true;
    if (Notification.isSupported()) {
      new Notification(notificationForHotkeyUnavailable()).show();
    }
  }
  return ok;
}

// ---- Screenshot hotkey window ----

function showHotkeyWindow() {
  if (hotkeyWindow) {
    hotkeyWindow.focus();
    return;
  }
  hotkeyWindow = new BrowserWindow({
    width: 420,
    height: 280,
    resizable: false,
    title: "Diariz - Screenshot hotkey",
    icon: ICON,
    webPreferences: {
      preload: path.join(__dirname, "hotkey-preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  hotkeyWindow.setMenuBarVisibility(false);
  hotkeyWindow.loadFile(path.join(__dirname, "hotkey.html"));
  hotkeyWindow.on("closed", () => {
    hotkeyWindow = null;
  });
}

ipcMain.handle("hotkey:load", () => normalizeAccelerator(store.get("captureHotkey")) ?? DEFAULT_ACCELERATOR);

// The sandboxed hotkey window can't require screenshotState.js itself, so it sends the
// raw KeyboardEvent descriptor (modifier booleans + e.code) here and gets back the
// accelerator formatted in Electron's own key vocabulary plus whether it currently
// validates. The descriptor carries `code` (the physical key), not `key` (the possibly
// shift-produced character) - `key` for Ctrl+Shift+3 is "#" and for Ctrl+Shift+9 is "(" on
// a US layout, so building from `key` let the shifted character sail through as a
// plausible-looking accelerator that only failed later at globalShortcut.register.
// `acceleratorFromKeyDescriptor` also flags a physical key Electron has no accelerator
// name for at all (`unsupported`) so this returns a clear message immediately instead of
// letting it through to fail the same way.
ipcMain.handle("hotkey:describe", (_event, descriptor) => {
  const { accelerator, unsupported } = acceleratorFromKeyDescriptor(descriptor);
  if (unsupported) {
    return { accelerator, valid: false, message: unsupportedKeyCaptureMessage() };
  }
  return { accelerator, valid: isValidAccelerator(accelerator) };
});

// Save only if the combination is both well-formed AND actually registrable - otherwise
// the user would set a hotkey that silently never fires because another app owns it.
// Registrability must be proven regardless of whether a recording is running right now:
//   - While `canCapture(recorder)` holds, `applyShortcut()` re-registering with the new
//     stored value IS the live registration - the same predicate that gates the tray menu
//     and the capture handler itself, so this window's behaviour can't drift from the
//     shortcut's actual lifecycle.
//   - While idle, nothing should be held (see applyShortcut's contract), so registrability
//     is proven with a transient probe register/unregister instead - proving the
//     combination works without leaving anything registered while idle.
ipcMain.handle("hotkey:save", (_event, accelerator) => {
  const normalized = normalizeAccelerator(accelerator);
  if (!normalized) return { ok: false, error: "Use at least one modifier (Ctrl, Alt, Shift) plus one key." };

  const previous = store.get("captureHotkey");
  store.set("captureHotkey", normalized);

  if (canCapture(recorder)) {
    if (!applyShortcut()) {
      if (previous) store.set("captureHotkey", previous);
      else store.delete("captureHotkey");
      applyShortcut();
      return { ok: false, error: hotkeyUnavailableSaveError() };
    }
    return { ok: true };
  }

  let registrable;
  try {
    registrable = globalShortcut.register(normalized, () => {});
    if (registrable) globalShortcut.unregister(normalized);
  } catch {
    registrable = false;
  }
  if (!registrable) {
    if (previous) store.set("captureHotkey", previous);
    else store.delete("captureHotkey");
    return { ok: false, error: hotkeyUnavailableSaveError() };
  }
  return { ok: true };
});

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
      else if (item.id === "record-both") startRecording("both");
      else if (item.id === "stop") stopRecording();
    },
  }));

  const shotItems = trayScreenshotItems({ ...recorder, autoCapture }).map((item) => ({
    label: item.label,
    enabled: item.enabled,
    ...(item.type ? { type: item.type, checked: item.checked } : {}),
    click: () => {
      if (item.id === "capture") void captureScreenshot();
      else if (item.id === "auto-capture") void toggleAutoCapture();
      else if (item.id === "change-area") void changeCaptureArea();
    },
  }));

  const outlookItems = trayOutlookItems(outlook).map((item) => ({
    label: item.label,
    enabled: item.enabled,
    click: () => {
      if (item.id === "outlook-sync") void syncOutlook();
    },
  }));

  const restart = updateRestartItem(update);
  const reload = trayReloadItem(targetUrl());

  tray.setToolTip(trayTooltip(recorder));
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: "Open Diariz", click: () => showMainWindow() },
      {
        label: "Open in Browser",
        click: () => {
          const u = targetUrl();
          if (u) shell.openExternal(u);
        },
      },
      // The only way to force a fresh document from inside the app: Windows runs menu-less, so Electron's
      // own Ctrl-R / Ctrl-Shift-R accelerators do not exist here, and closing the window merely hides it to
      // this tray. Without this, a shell stuck on a stale build could only be fixed by deleting its cache
      // directory by hand.
      ...(reload ? [{ label: reload.label, click: reloadMainWindow }] : []),
      ...(restart ? [{ label: restart.label, click: restartToUpdate }] : []),
      { type: "separator" },
      ...recordItems,
      ...shotItems,
      ...outlookItems,
      { type: "separator" },
      {
        label: process.platform === "darwin" ? "Open at Login" : "Start with Windows",
        type: "checkbox",
        checked: openAtLogin(),
        click: toggleOpenAtLogin,
      },
      { label: "Check for Updates…", click: () => checkForUpdates(true) },
      { label: "Screenshot Hotkey…", click: () => showHotkeyWindow() },
      { label: "Settings…", click: () => showSetupWindow() },
      // No confirmation here: `app.quit()` fires `before-quit`, which is the single gate. Asking in both
      // places would ask twice.
      { label: "Quit", click: () => app.quit() },
    ]),
  );
}

/// Ask before a quit that would destroy a recording. Returns true when the quit should go ahead.
///
/// Every quit path funnels through here rather than only the tray item: `before-quit` also fires for the
/// macOS app menu, the dock, and a signal, and a guard on one entry point is not a guard.
/// Latched once the user has said yes, so a path that confirms and then calls `app.quit()` is not asked a
/// second time by `before-quit`. Never reset: the app is on its way out.
let quitConfirmed = false;

function confirmQuit() {
  if (quitConfirmed) return true;
  const ask = quitConfirmation(recorder);
  if (!ask) return true;
  // Synchronous on purpose: `before-quit` cannot be paused, so the answer has to arrive before it returns.
  const ok = dialog.showMessageBoxSync(mainWindow ?? undefined, ask) === 0;
  if (ok) quitConfirmed = true;
  return ok;
}

/// macOS menu-bar icon: a monochrome Template image (black-on-transparent) that macOS recolours for the
/// light/dark menu bar. Named `...Template` and flagged, so it's icon-only (no text - a title alongside the
/// icon made the item too wide and it fell behind the notch). The @2x variant is picked up automatically.
function macTrayIcon() {
  const img = nativeImage.createFromPath(path.join(__dirname, "..", "build", "trayTemplate.png"));
  img.setTemplateImage(true);
  return img;
}

function buildTray() {
  const trayIcon =
    process.platform === "darwin"
      ? macTrayIcon()
      : ICON.isEmpty()
        ? ICON
        : ICON.resize({ width: 16, height: 16 });
  tray = new Tray(trayIcon);
  // Windows: left-click opens the window, right-click shows the menu. macOS: a click shows the menu-bar
  // dropdown (don't bind a click handler or it steals the click; the menu's "Open Diariz" opens the window).
  if (process.platform !== "darwin") tray.on("click", () => showMainWindow());
  refreshTray();
}

ipcMain.on("recorder:state", (_event, state) => {
  if (state && typeof state.phase === "string") applyRecorderState(state);
});

ipcMain.handle("auth:start-google", () => startGoogleSignIn());

// ---- App lifecycle ----

// Single-instance: a second launch focuses the running app instead of starting another.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  // Windows shows this as the toast attribution and groups taskbar/notifications;
  // without it, notifications are titled "Electron". Match the installer's appId. Windows-only.
  if (process.platform === "win32") app.setAppUserModelId("com.diariz.desktop");

  // Own the diariz:// scheme so Google sign-in deep links come back to this app. In dev (unpackaged)
  // Windows needs the explicit exec path + script arg; packaged builds register it via the installer.
  if (process.defaultApp && process.argv.length >= 2) {
    app.setAsDefaultProtocolClient("diariz", process.execPath, [path.resolve(process.argv[1])]);
  } else {
    app.setAsDefaultProtocolClient("diariz");
  }

  app.on("second-instance", (_e, argv) => {
    showMainWindow();
    void handleAuthDeepLink(argv);
  });

  app.on("open-url", (e, url) => {
    e.preventDefault();
    void handleAuthDeepLink([url]);
  });

  app.whenReady().then(() => {
    // macOS needs a real app menu for the standard shortcuts (Cmd-Q to quit, Cmd-C/V/X/A in text fields
    // like the setup URL / login, Cmd-M/W). On Windows this stays a menu-less tray shell.
    if (process.platform === "darwin") {
      Menu.setApplicationMenu(
        Menu.buildFromTemplate([{ role: "appMenu" }, { role: "editMenu" }, { role: "windowMenu" }]),
      );
    } else {
      Menu.setApplicationMenu(null);
    }
    // Whether this build can reach Outlook at all (Windows, the bundled reader is present, and classic
    // Outlook is actually installed). Resolved once here so the tray and the web app agree; the user's opt-in
    // arrives separately from the renderer. Not awaited - the tray must not wait on a subprocess, and the
    // launch sync is licensed by the renderer's `outlook:ready`, which arrives well after this settles.
    void outlookAvailability();
    outlook.lastSyncAt = store.get("outlookLastSyncAt") || 0;

    buildTray();
    if (targetUrl()) createMainWindow(targetUrl());
    else showSetupWindow();

    // The pre-warmed capture overlays are sized and positioned per display, so a monitor
    // plugged in, unplugged, or re-resolutioned mid-recording has to re-fit them - a stale
    // pool would leave a screen unpickable or float an overlay over the wrong geometry.
    // (`screen` is only usable once the app is ready, hence registering here.)
    for (const event of ["display-added", "display-removed", "display-metrics-changed"]) {
      screen.on(event, () => {
        syncPickerWarmth();
        syncAutoCapture();
      });
    }

    setupAutoUpdater();
    void handleAuthDeepLink(process.argv); // cold start launched by a deep link
    app.on("activate", () => showMainWindow());
  });

  app.on("before-quit", (e) => {
    // Covers the quit paths that never touch the tray item - the macOS app menu, the dock, Cmd-Q.
    if (!confirmQuit()) {
      e.preventDefault();
      return;
    }
    isQuitting = true;
    // Closed explicitly rather than left to teardown, so its `close` handler runs and the bounds are
    // saved for next time.
    closeNotesPopout();
  });

  // The shortcut is scoped to a recording; make sure it never outlives the app either.
  // The picker overlays are symmetric cleanup - don't leave them running past quit.
  app.on("will-quit", () => {
    globalShortcut.unregisterAll();
    teardownPickers();
  });

  // Tray-resident: keep running when all windows are closed/hidden.
  app.on("window-all-closed", () => {});
}
