"use strict";

/// How the shell loads - and re-loads - the web app's document.
///
/// This shell owns no UI of its own: it points a BrowserWindow at a server origin and the SPA does the rest.
/// That makes the freshness of one file, the document, the difference between running this week's app and
/// last week's - and for three releases running it was last week's. The server had the fix, the user had a
/// cached shell naming the previous build's content-hashed bundles, and nothing in the app could break the
/// tie: Windows runs menu-less (`Menu.setApplicationMenu(null)`), so Electron's own Ctrl-R / Ctrl-Shift-R
/// accelerators do not exist, and closing the window only hides it to the tray, so "restarting" it usually
/// did not.

/// Options for `webContents.loadURL`. The request header makes the cache revalidate the document rather than
/// serving it blind; with the server's own `no-cache` in place the answer is a 304 on about a kilobyte.
///
/// Deliberately the DOCUMENT only. Clearing the session cache would work too and is what most answers on the
/// internet suggest, but it would also throw away the content-hashed `/assets/` bundles - megabytes that are
/// immutable by construction and correct to keep forever.
function documentLoadOptions() {
  // Newline-terminated: Chromium parses `extraHeaders` as a header block and drops an unterminated last line.
  return { extraHeaders: "pragma: no-cache\n" };
}

/// The tray's Reload item, or null when no server is configured yet and there is nothing to reload. Mirrors
/// `updateRestartItem`: the menu shows an entry only when it would actually do something.
function trayReloadItem(targetUrl) {
  if (!targetUrl) return null;
  return { id: "reload", label: "Reload" };
}

module.exports = { documentLoadOptions, trayReloadItem };
