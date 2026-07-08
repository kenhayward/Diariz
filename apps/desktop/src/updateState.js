"use strict";

// Pure model for the auto-updater's user-facing bits. `main.js` owns the
// electron-updater wiring; the menu item and notification copy live here so they
// can be unit-tested without a packaged build.

/// The "Restart to update (vX)" tray item, or null when no update is waiting.
/// state: { ready: boolean, version?: string }
function updateRestartItem(state) {
  if (!state || !state.ready) return null;
  return {
    id: "restart-update",
    label: state.version ? `Restart to update (${state.version})` : "Restart to update",
  };
}

/// What native notification (if any) an updater event should raise.
/// `kind`: "available" | "not-available" | "downloaded" | "error".
/// `manual`: true when the user explicitly hit "Check for Updates…" (so we give
/// feedback even when there's nothing to do). Automatic checks stay quiet unless an
/// update actually downloaded. Returns { title, body } or null.
function notificationForUpdate(kind, opts = {}) {
  const { version, manual } = opts;
  switch (kind) {
    case "downloaded":
      return {
        title: "Diariz",
        body: version ? `Update ${version} is ready — restart to update` : "An update is ready — restart to update",
      };
    case "available":
      return manual
        ? { title: "Diariz", body: version ? `Downloading update ${version}…` : "Downloading update…" }
        : null;
    case "not-available":
      return manual
        ? { title: "Diariz", body: version ? `You are on the latest version ${version}` : "You are on the latest version" }
        : null;
    case "error":
      return manual ? { title: "Diariz", body: "Update check failed" } : null;
    default:
      return null;
  }
}

/// True when `latest` is a strictly greater Major.Minor.Build than `current`. Used by the macOS manual
/// "Check for Updates" (which compares the app version against the latest GitHub release tag, since
/// Squirrel.Mac auto-update needs a signed build). Tolerates a leading "v"/whitespace on the tag; returns
/// false for missing/unparseable input so a bad API response never prompts a phantom update.
function isNewerVersion(current, latest) {
  const parse = (v) => {
    if (typeof v !== "string") return null;
    const s = v.trim().replace(/^v/i, "");
    if (s === "") return null;
    // Number("") is 0, so reject empty segments explicitly ("", "0.105." etc. are not versions).
    const nums = s.split(".").map((p) => (p === "" ? NaN : Number(p)));
    return nums.every((n) => Number.isInteger(n) && n >= 0) ? nums : null;
  };
  const a = parse(current);
  const b = parse(latest);
  if (!a || !b) return false;
  const len = Math.max(a.length, b.length);
  for (let i = 0; i < len; i++) {
    const x = a[i] ?? 0;
    const y = b[i] ?? 0;
    if (y > x) return true;
    if (y < x) return false;
  }
  return false; // equal
}

module.exports = { updateRestartItem, notificationForUpdate, isNewerVersion };
