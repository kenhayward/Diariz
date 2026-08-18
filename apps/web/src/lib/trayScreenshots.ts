import type { CaptureArea } from "./captureGeometry";

/// The seam between the Electron shell's screen capture and the web recorder. Mirrors trayRecorder: the
/// shell owns the capture (hotkey, tray, capture area), the web app owns the recording clock. Everything
/// here is a no-op in a plain browser, so callers never have to branch on `isElectron`.

export interface CapturedShot {
  full: Blob;
  thumb: Blob;
  width: number;
  height: number;
}

// The shell hands captures over Electron's structured-clone IPC. Node Buffers (from
// nativeImage.toPNG()/toJPEG() in the main process) arrive here as Uint8Array, not ArrayBuffer - see
// preload.js's onScreenshotCaptured JSDoc for why.
interface ShellPayload {
  full: Uint8Array;
  thumb: Uint8Array;
  width: number;
  height: number;
}

/// What the shell reports when auto-capture starts or stops. `area` is present only while active, and
/// describes what to capture in the target display's physical pixels - the renderer opens its own
/// getDisplayMedia stream against the display the shell has already granted.
export interface AutoCaptureState {
  active: boolean;
  area?: CaptureArea;
}

interface ScreenshotShell {
  canCaptureScreenshot?: boolean;
  captureScreenshot?: () => void;
  changeCaptureArea?: () => void;
  hasCaptureArea?: () => Promise<boolean>;
  onCaptureAreaChanged?: (cb: (hasArea: boolean) => void) => () => void;
  onScreenshotCaptured?: (cb: (payload: ShellPayload) => void) => () => void;
  toggleAutoCapture?: () => void;
  onAutoCaptureChanged?: (cb: (state: AutoCaptureState) => void) => () => void;
}

function shell(): ScreenshotShell | undefined {
  return (window as { diariz?: ScreenshotShell }).diariz;
}

/// True when this build can capture screenshots (the desktop shell). Drives whether the UI shows any
/// capture affordance at all.
export function canCaptureScreenshots(): boolean {
  return shell()?.canCaptureScreenshot === true;
}

/// Subscribe to captures from the shell. Returns an unsubscribe function (a no-op in a browser).
export function onScreenshotCaptured(cb: (shot: CapturedShot) => void): () => void {
  const api = shell();
  if (!api?.onScreenshotCaptured) return () => {};
  return api.onScreenshotCaptured((payload) =>
    cb({
      // TS's lib.dom types BlobPart's ArrayBufferView as generic over ArrayBuffer specifically, while a
      // structured-clone Uint8Array is typed over the wider ArrayBufferLike - a type-only mismatch, not a
      // runtime one (Blob happily accepts a Uint8Array), so a cast is the correct fix here.
      full: new Blob([payload.full as BlobPart], { type: "image/png" }),
      thumb: new Blob([payload.thumb as BlobPart], { type: "image/jpeg" }),
      width: payload.width,
      height: payload.height,
    }),
  );
}

export function requestCapture(): void {
  shell()?.captureScreenshot?.();
}

export function requestChangeArea(): void {
  shell()?.changeCaptureArea?.();
}

/// Whether the current recording already has a capture area. Capturing without one opens the shell's area
/// picker and leaves the capture controls inert until it is dismissed, so the UI gates capture on this.
/// An older shell (one without the bridge) reports true, keeping its existing pick-on-first-capture flow.
export async function hasCaptureArea(): Promise<boolean> {
  const api = shell();
  if (!api?.hasCaptureArea) return true;
  return api.hasCaptureArea();
}

/// Subscribe to capture-area changes. Returns an unsubscribe function (a no-op in a browser or an older shell).
export function onCaptureAreaChanged(cb: (hasArea: boolean) => void): () => void {
  const api = shell();
  if (!api?.onCaptureAreaChanged) return () => {};
  return api.onCaptureAreaChanged(cb);
}

/// Whether this shell can auto-capture. Separate from `canCaptureScreenshots` on purpose: an installed
/// desktop app updates on its own schedule, so a shell that predates this feature is a normal state
/// rather than an error, and the toggle simply does not appear on it.
export function canAutoCapture(): boolean {
  return typeof shell()?.toggleAutoCapture === "function";
}

/// Ask the shell to start or stop auto-capture. With no capture area chosen yet the shell opens the
/// picker first, exactly as a manual capture does.
export function requestToggleAutoCapture(): void {
  shell()?.toggleAutoCapture?.();
}

/// Subscribe to auto-capture starting and stopping. This is an event rather than something to poll
/// because auto-capture stops on its own too: the recording ends, or the capture area's display goes
/// away. Returns an unsubscribe function (a no-op in a browser or an older shell).
export function onAutoCaptureChanged(cb: (state: AutoCaptureState) => void): () => void {
  const api = shell();
  if (!api?.onAutoCaptureChanged) return () => {};
  return api.onAutoCaptureChanged(cb);
}
