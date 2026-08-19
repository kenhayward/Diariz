/// Recovery from a stale JS chunk after a deploy.
///
/// Vite gives every chunk a content-hashed filename, and deploying replaces the whole set - the previous
/// files are gone. A session that loaded BEFORE a deploy still holds the old names in its downloaded module
/// graph, so the first `lazy(() => import(...))` for a page it has not opened yet asks for a file the server
/// no longer has and gets a 404. Nothing recovers on its own: the import rejects, the ErrorBoundary catches
/// it, and every one of the app's lazily-loaded pages stays broken until someone reloads by hand.
///
/// It reads as a desktop-only bug, and is not one. The desktop shell hides to tray rather than closing, so it
/// can run for days across several deploys; a browser tab gets reloaded far more often, which is the only
/// reason it is rarely seen there. Nothing in `apps/desktop` is involved.
///
/// Vite's own preload helper anticipates this: on a failed preload OR a failed dynamic import it dispatches a
/// **cancelable** `vite:preloadError` on `window` and rethrows only if nobody calls `preventDefault()`.
/// Reloading fetches the current `index.html` (served `no-cache`) and with it the current chunk names.

/// When the last automatic reload happened, in `sessionStorage` so it survives the reload it triggered.
const RELOADED_AT_KEY = "diariz.chunkReloadedAt";

/// How long after an automatic reload a second failure is treated as "reloading did not help" rather than
/// "stale chunk". This is not paranoia: if the missing chunk belongs to the route the user is ALREADY on,
/// the reload re-runs the same failing import, which would reload again, forever. Past this window the guard
/// lifts, so a later deploy still recovers on its own.
const LOOP_WINDOW_MS = 10_000;

/// Set by the Recorder around a capture. Module state rather than React state because the listener below is
/// installed once at startup, outside the component tree, and has to read this at the moment of failure.
let capturing = false;

/// Called by the Recorder when a recording or upload starts and finishes.
export function setCapturing(active: boolean): void {
  capturing = active;
}

export interface ChunkReloadDeps {
  reload: () => void;
  storage: Pick<Storage, "getItem" | "setItem">;
  now?: () => number;
  /// True while a recording or upload is in flight. A reload tears the page down and `MediaRecorder` lives
  /// in it, so reloading then would destroy the take - and the desktop tray app is exactly where long
  /// unattended recordings happen. Defaults to the flag `setCapturing` maintains.
  isCapturing?: () => boolean;
}

/// Decide and act on one preload failure.
/// @returns true when it reloaded - the caller should then cancel the event so Vite does not also rethrow.
export function handlePreloadError(deps: ChunkReloadDeps): boolean {
  const now = deps.now ?? (() => Date.now());
  const isCapturing = deps.isCapturing ?? (() => capturing);

  if (isCapturing()) return false;

  const last = Number(deps.storage.getItem(RELOADED_AT_KEY) ?? 0);
  // NaN from a hand-edited value compares false here, which correctly falls through to reloading.
  if (now() - last < LOOP_WINDOW_MS) return false;

  deps.storage.setItem(RELOADED_AT_KEY, String(now()));
  deps.reload();
  return true;
}

/// Listen for Vite's preload failures. Returns a function that stops listening.
export function installChunkReloadHandler(deps: ChunkReloadDeps): () => void {
  const onPreloadError = (event: Event) => {
    // Only cancel when we actually reloaded. Declining and cancelling would swallow the error and leave the
    // user looking at a page that silently did nothing.
    if (handlePreloadError(deps)) event.preventDefault();
  };
  window.addEventListener("vite:preloadError", onPreloadError);
  return () => window.removeEventListener("vite:preloadError", onPreloadError);
}

/// The wording engines use when a dynamic import cannot be fetched. Chrome and Firefox differ, and Safari
/// differs again, so all three are matched rather than the one seen in the original report.
const STALE_CHUNK_PATTERNS = [
  "failed to fetch dynamically imported module",
  "error loading dynamically imported module",
  "importing a module script failed",
  "unable to preload css",
];

/// Whether a caught error is a missing-chunk failure rather than an ordinary crash. Used by the
/// ErrorBoundary to offer a reload instead of a dead end - deliberately narrow, because calling every render
/// crash "a new version is available" would be untrue and would hide real bugs behind a Reload button.
export function isStaleChunkError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  const lower = message.toLowerCase();
  return STALE_CHUNK_PATTERNS.some((pattern) => lower.includes(pattern));
}
