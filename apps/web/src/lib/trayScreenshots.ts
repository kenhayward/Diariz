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

interface ScreenshotShell {
  canCaptureScreenshot?: boolean;
  captureScreenshot?: () => void;
  changeCaptureArea?: () => void;
  onScreenshotCaptured?: (cb: (payload: ShellPayload) => void) => () => void;
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
