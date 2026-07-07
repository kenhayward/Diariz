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

module.exports = { updateRestartItem, notificationForUpdate };
