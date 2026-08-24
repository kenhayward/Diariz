"use strict";

// Pure model for the download handler's user-facing bit. `main.js` owns the Electron `will-download`
// wiring; the notification decision lives here so it can be unit-tested without a packaged build.
//
// Progress copy is deliberately NOT here. The handler forwards raw byte counts and the web app turns
// them into text: it already has `formatBytes` and the locale catalogs, and a second size formatter
// in a second language would only drift.

/// How long a download must run before finishing is worth an OS notification. Below this you are still
/// looking at the window that started it, and the handler covers every download in the app - a 5 KB
/// transcript announcing itself would be worse than the silence this replaces.
const NOTIFY_AFTER_MS = 5000;

/// What native notification (if any) a finished download should raise.
/// `state`: Electron's done-state, "completed" | "cancelled" | "interrupted".
/// Returns { title, body } or null.
function notificationForDownload(state, opts = {}) {
  const { filename, elapsedMs } = opts || {};
  if (!filename) return null;
  switch (state) {
    case "completed":
      return elapsedMs > NOTIFY_AFTER_MS ? { title: "Diariz", body: `Saved ${filename}` } : null;
    // A failure is the case you must not miss, and it tends to fail fast - so it ignores the threshold.
    case "interrupted":
      return { title: "Diariz", body: `Download failed - ${filename}` };
    // Cancelling is the user's own doing; telling them about it is noise.
    case "cancelled":
    default:
      return null;
  }
}

module.exports = { notificationForDownload, NOTIFY_AFTER_MS };
