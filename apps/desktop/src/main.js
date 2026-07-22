"use strict";

const path = require("node:path");
const crypto = require("node:crypto");
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
} = require("electron");
const Store = require("electron-store");
const { normalizeServerUrl } = require("./url");
const { trayRecorderItems, trayTooltip, notificationFor } = require("./recorderState");
const { updateRestartItem, notificationForUpdate, isNewerVersion } = require("./updateState");
const { buildStartUrl, codeFromArgv, notificationForAuthError } = require("./desktopAuth");
const { cropRectFor, resizeDims, clampRect } = require("./captureTarget");
const {
  trayScreenshotItems,
  DEFAULT_ACCELERATOR,
  normalizeAccelerator,
  canCapture,
  shouldStartCapture,
  notificationForCaptureFailure,
  notificationForHotkeyUnavailable,
} = require("./screenshotState");

// In dev we load the Vite dev server directly and skip first-run setup.
const DEV_URL = process.env.DIARIZ_DEV ? "http://localhost:5173" : null;

const store = new Store({ name: "diariz-config" });
const ICON = nativeImage.createFromPath(path.join(__dirname, "..", "build", "icon.png"));

let tray = null;
let mainWindow = null;
let setupWindow = null;
let hotkeyWindow = null;
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

  // Close to tray/menu bar rather than quitting (this is a tray-resident app).
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

  if (next.phase === "recording" && prev.phase !== "recording") {
    recordingStartedAt = Date.now();
    captureTarget = null; // each recording chooses its own capture area
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
  refreshTray();
}

function setRecorderReady(ready) {
  if (recorder.ready === ready) return;
  recorder.ready = ready;
  // `ready` flipping false (reload, window close) must drop a held shortcut immediately -
  // `recorder.phase` alone goes stale here, so re-evaluate the gate now, not just on the
  // next phase report.
  applyShortcut();
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
// Open picker windows, keyed by display id, plus the promise waiting on a choice.
let pickerWindows = new Map();
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

function closePickers() {
  for (const win of pickerWindows.values()) if (!win.isDestroyed()) win.destroy();
  pickerWindows = new Map();
}

/// Show a full-screen overlay on every display and resolve with the chosen target
/// ({ displayId, selection }) or null if the user cancelled. If a picker is already
/// showing, returns its existing promise instead of starting a second one.
function openPicker() {
  if (pickerPromise) return pickerPromise;
  closePickers(); // defensive: clear any stray windows from a picker that didn't settle cleanly
  const attempt = new Promise((resolve, reject) => {
    try {
      pickerResolve = resolve;
      // The overlay's only cancel path is an Escape keydown handler inside its own
      // window, so one of the overlays MUST hold OS keyboard focus once shown - pick
      // the display the cursor is already on so Escape reaches it immediately.
      const cursorDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
      for (const display of screen.getAllDisplays()) {
        const isCursorDisplay = display.id === cursorDisplay.id;
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
        // Focusing synchronously right after creation can be dropped if the native
        // window isn't realized yet, leaving Escape unreachable. Wait for
        // ready-to-show, then show the cursor's display active (and focused) and the
        // rest inactive, so nothing steals foreground focus from the intended overlay.
        win.once("ready-to-show", () => {
          if (isCursorDisplay) {
            win.show();
            win.focus();
          } else {
            win.showInactive();
          }
        });
        // Self-heal: if this overlay never manages to show anything (picker.html fails
        // to load), close it rather than leaving a dead window around forever. Once
        // every picker window has been destroyed - by this, by the user closing them,
        // or by settlePicker itself - resolve with null instead of hanging, so a picker
        // that never settles can't wedge every future capture attempt.
        win.webContents.on("did-fail-load", () => {
          if (!win.isDestroyed()) win.destroy();
        });
        win.on("closed", () => {
          pickerWindows.delete(display.id);
          if (pickerResolve && pickerWindows.size === 0) settlePicker(null);
        });
        win.setAlwaysOnTop(true, "screen-saver");
        win.loadFile(path.join(__dirname, "picker.html"));
        pickerWindows.set(display.id, win);
      }
    } catch (err) {
      reject(err);
    }
  });
  // If the executor above threw (e.g. screen.getCursorScreenPoint()/new BrowserWindow
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
  closePickers();
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
      captureTarget = await openPicker();
      if (!captureTarget) return; // cancelled - no capture, no error
    }
    // The picker await is unbounded (the user may sit on the overlay) and grab() is
    // another await - re-assert the gate right before touching the renderer rather than
    // trusting the check from the top of the function.
    if (!canCapture(recorder) || !mainWindow) return;
    const shot = await grab(captureTarget);
    if (!shot) {
      captureTarget = null; // display gone, or crop degenerated: re-prompt on the next capture
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
  captureTarget = null;
  if (!canCapture(recorder)) return;
  try {
    captureTarget = await openPicker();
    if (!canCapture(recorder)) captureTarget = null; // recording ended while the picker was open
  } catch {
    notifyCaptureFailed("error");
  }
}

ipcMain.handle("screenshot:capture", () => captureScreenshot());
ipcMain.handle("screenshot:change-area", () => changeCaptureArea());

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
      return { ok: false, error: "That combination is already in use by another application." };
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
    return { ok: false, error: "That combination is already in use by another application." };
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

  const shotItems = trayScreenshotItems(recorder).map((item) => ({
    label: item.label,
    enabled: item.enabled,
    click: () => {
      if (item.id === "capture") void captureScreenshot();
      else if (item.id === "change-area") void changeCaptureArea();
    },
  }));

  const restart = updateRestartItem(update);

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
      ...(restart ? [{ label: restart.label, click: restartToUpdate }] : []),
      { type: "separator" },
      ...recordItems,
      ...shotItems,
      { type: "separator" },
      {
        label: process.platform === "darwin" ? "Open at Login" : "Start with Windows",
        type: "checkbox",
        checked: openAtLogin(),
        click: toggleOpenAtLogin,
      },
      { label: "Check for Updates…", click: () => checkForUpdates(true) },
      { label: "Screenshot Hotkey...", click: () => showHotkeyWindow() },
      { label: "Settings…", click: () => showSetupWindow() },
      { label: "Quit", click: () => { isQuitting = true; app.quit(); } },
    ]),
  );
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
    buildTray();
    if (targetUrl()) createMainWindow(targetUrl());
    else showSetupWindow();

    setupAutoUpdater();
    void handleAuthDeepLink(process.argv); // cold start launched by a deep link
    app.on("activate", () => showMainWindow());
  });

  app.on("before-quit", () => {
    isQuitting = true;
  });

  // The shortcut is scoped to a recording; make sure it never outlives the app either.
  // Any open picker overlay is symmetric cleanup - don't leave it running past quit.
  app.on("will-quit", () => {
    globalShortcut.unregisterAll();
    closePickers();
  });

  // Tray-resident: keep running when all windows are closed/hidden.
  app.on("window-all-closed", () => {});
}
