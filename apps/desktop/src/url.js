"use strict";

/// Normalise a user-entered server address into an origin we can load the web app from.
/// Defaults a bare host to https, drops any path/query/hash, and rejects non-http(s) input.
/// Returns the origin (e.g. "https://diariz.example.com") or null when invalid.
function normalizeServerUrl(input) {
  const raw = (input || "").trim();
  if (!raw) return null;
  const hasHttp = /^https?:\/\//i.test(raw);
  // An explicit non-http(s) scheme (ftp://, file://, …) is rejected rather than coerced.
  if (!hasHttp && /^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) return null;
  const withScheme = hasHttp ? raw : `https://${raw}`;
  try {
    const u = new URL(withScheme);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    if (!u.hostname) return null;
    return u.origin;
  } catch {
    return null;
  }
}

/// Whether a link the page asked to open in a NEW window belongs to the system browser rather than to a
/// window of our own.
///
/// `setWindowOpenHandler` used to send every target to `shell.openExternal`, while `will-navigate`
/// directly beside it correctly externalised only cross-origin ones. That asymmetry is what sent in-app
/// links to the system browser, where the user is not signed in and the app's pages sit behind the login -
/// so clicking a `?` popover's "read more" asked you to log in again to read a help article. The web side
/// of this was fixed in PR #731 by making the About-box links navigate in place; HelpPopover deliberately
/// keeps its separate window (it is opened from modals holding unsaved state, which navigating would
/// discard), so it needs the window to be OURS rather than the browser's.
///
/// Same-origin http(s) stays with us. Everything else leaves: another site, and non-http schemes
/// (`mailto:`, `tel:`) which belong to the OS by definition. An unparseable target is treated as external,
/// which is exactly what the old unconditional behaviour did - no change on an edge nobody has hit.
function opensExternally(target, appOrigin) {
  let u;
  try {
    u = new URL(target);
  } catch {
    return true;
  }
  if (u.protocol !== "http:" && u.protocol !== "https:") return true;
  return u.origin !== appOrigin;
}

module.exports = { normalizeServerUrl, opensExternally };
