const { execFile } = require("node:child_process");
const path = require("node:path");
const fs = require("node:fs");

/// Runs the bundled Outlook reader and parses what it says.
///
/// Thin on purpose: everything decision-shaped lives in the pure outlookSync.js. A separate process (rather
/// than a native Node addon) keeps the Electron shell free of native modules, keeps a tens-of-seconds COM read
/// off main's event loop - which would otherwise freeze the tray, the recorder IPC and the screenshot hotkey -
/// and means a hung or crashing COM call kills the helper, not Diariz.

/// Generous, because a first read of a large mailbox with bodies genuinely takes a while, but finite: a COM
/// call waiting on a modal dialog in Outlook would otherwise hang forever.
const READ_TIMEOUT_MS = 120_000;

/// stdout is one JSON document; a huge mailbox with bodies can be a few MB.
const MAX_OUTPUT_BYTES = 64 * 1024 * 1024;

/// Where the helper sits in a packaged app (electron-builder `extraResources`) and in a dev checkout.
function readerPath() {
  const packaged = path.join(process.resourcesPath || "", "outlook", "Diariz.OutlookReader.exe");
  if (fs.existsSync(packaged)) return packaged;
  return path.join(__dirname, "..", "native", "publish", "Diariz.OutlookReader.exe");
}

/// True when this build could read Outlook at all: Windows, and the helper is actually present. Checked rather
/// than assumed, because a dev checkout that has not published the helper is a normal state.
function isAvailable() {
  return process.platform === "win32" && fs.existsSync(readerPath());
}

/// A probe of the reader is registry-only, so it is quick - but it is still a process launch, and it runs on
/// the path that decides whether to show the connector at all.
const PROBE_TIMEOUT_MS = 15_000;

/// Ask the reader whether classic Outlook is installed, WITHOUT activating it.
///
/// This exists because activation is the harmful part: on a PC with Office but no classic Outlook, creating
/// `Outlook.Application` makes Windows offer to install it, and Diariz syncs on launch - so those users met
/// that dialog every time they opened the app. The reader answers from the registry instead.
///
/// Always resolves with `{ ok, reason? }`.
function probe() {
  return new Promise((resolve) => {
    if (process.platform !== "win32") {
      resolve({ ok: false, reason: "not-windows" });
      return;
    }
    const exe = readerPath();
    if (!fs.existsSync(exe)) {
      resolve({ ok: false, reason: "unavailable" });
      return;
    }

    execFile(exe, ["--probe"], { timeout: PROBE_TIMEOUT_MS, windowsHide: true }, (error, stdout) => {
      try {
        const parsed = JSON.parse(stdout);
        resolve({ ok: parsed.ok === true, reason: parsed.reason });
      } catch {
        // A reader that cannot even answer this is not one to hand a calendar read to. Reported as a
        // transient error rather than "not installed", so it is not remembered across launches.
        resolve({ ok: false, reason: error && error.killed ? "timeout" : "error" });
      }
    });
  });
}

/// Read a window of the calendar.
///
/// Always resolves - never rejects - with `{ ok, reason?, error?, complete, events, mailboxName,
/// deviceTimeZone }`. A failure is a state the UI explains, not an exception to handle at every call site.
function read({ start, end, skipPrivate = true, includeBody = true, max = 5000 } = {}) {
  return new Promise((resolve) => {
    if (process.platform !== "win32") {
      resolve({ ok: false, reason: "not-windows", complete: false, events: [] });
      return;
    }
    const exe = readerPath();
    if (!fs.existsSync(exe)) {
      resolve({ ok: false, reason: "unavailable", complete: false, events: [] });
      return;
    }

    const args = ["--start", start, "--end", end, "--max", String(max)];
    // The reader's flags are opt-out, so its safer defaults hold unless we ask otherwise.
    if (!skipPrivate) args.push("--include-private");
    if (!includeBody) args.push("--no-body");

    execFile(
      exe,
      args,
      { timeout: READ_TIMEOUT_MS, maxBuffer: MAX_OUTPUT_BYTES, windowsHide: true },
      (error, stdout) => {
        // A timeout kills the child, and its own reason matters more than whatever half-output it managed.
        if (error && error.killed) {
          resolve({ ok: false, reason: "timeout", complete: false, events: [] });
          return;
        }

        // The reader exits non-zero on failure but still writes a document saying why, so parse before
        // judging the exit code.
        try {
          const parsed = JSON.parse(stdout);
          resolve({ complete: false, events: [], ...parsed });
        } catch {
          resolve({
            ok: false,
            reason: "error",
            error: error ? String(error.message || error) : "The Outlook reader returned nothing readable.",
            complete: false,
            events: [],
          });
        }
      },
    );
  });
}

module.exports = { read, probe, isAvailable, readerPath, READ_TIMEOUT_MS };
