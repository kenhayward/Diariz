/// Whether a recording or upload is in flight, as a module-level flag rather than React state: the things
/// that need it are guards installed once at startup, outside the component tree, which have to read it at
/// the moment something is about to destroy the page.
///
/// Two guards read it, which is why it lives here rather than inside either of them:
///   - `chunkReload` must not reload the page to pick up a new build mid-capture.
///   - `unloadGuard` must ask before the browser leaves the page mid-capture.
///
/// "Uploading" counts as in flight. The blob is stashed to local storage just before the request goes out
/// (`savePendingRecording`), so most of that phase is recoverable - but not the moment before the stash, and
/// losing it still costs the user a recovery round trip they never asked for.
let capturing = false;

/// Called by the Recorder on every phase change. See its `report()` helper - the single place phases are
/// published, so this cannot drift from what the tray is told.
export function setCapturing(active: boolean): void {
  capturing = active;
}

export function isCapturing(): boolean {
  return capturing;
}
