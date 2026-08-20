import { isCapturing as capturing } from "./captureState";

/// Asks before the page is torn down while a recording or upload is in flight.
///
/// A live recording exists only as chunks in this page's memory until the recorder stops - a reload, a
/// navigation or a closed tab loses the meeting outright, with nothing stashed and nothing to recover. An
/// upload is nearly safe (the blob is written to local storage just before the request), but not quite, and
/// interrupting it still costs a recovery round trip.
export interface UnloadGuardDeps {
  isCapturing?: () => boolean;
  /// Whether we are inside the Electron shell. **Not** a cosmetic difference: Electron cancels a window
  /// close as soon as `beforeunload` is handled and shows no dialog of its own, so guarding here would make
  /// the tray's Quit silently do nothing - a broken-looking app traded for a data-loss bug. The desktop
  /// confirms in its main process instead (`quitConfirmation` in apps/desktop), where it can actually ask.
  /// A normal desktop window close never reaches this anyway: the shell hides to tray rather than unloading.
  isDesktop?: () => boolean;
}

function inDesktop(): boolean {
  return Boolean((window as { diariz?: { isElectron?: boolean } }).diariz?.isElectron);
}

export function shouldBlockUnload(deps: UnloadGuardDeps = {}): boolean {
  const isDesktop = deps.isDesktop ?? inDesktop;
  if (isDesktop()) return false;
  return (deps.isCapturing ?? capturing)();
}

/// Install the guard. Returns a function that removes it.
export function installUnloadGuard(deps: UnloadGuardDeps = {}): () => void {
  const onBeforeUnload = (event: BeforeUnloadEvent) => {
    if (!shouldBlockUnload(deps)) return;
    // preventDefault is the modern spelling; returnValue is still what some engines actually check.
    event.preventDefault();
    event.returnValue = "";
  };
  window.addEventListener("beforeunload", onBeforeUnload);
  return () => window.removeEventListener("beforeunload", onBeforeUnload);
}
