import { afterEach, describe, expect, it, vi } from "vitest";
import {
  canCaptureScreenshots,
  onScreenshotCaptured,
  requestCapture,
  requestChangeArea,
  requestToggleAutoCapture,
  onAutoCaptureChanged,
  canAutoCapture,
} from "./trayScreenshots";

declare global {
  interface Window {
    diariz?: unknown;
  }
}

afterEach(() => {
  delete window.diariz;
});

describe("trayScreenshots", () => {
  it("reports no capture support in a plain browser", () => {
    expect(canCaptureScreenshots()).toBe(false);
  });

  it("reports capture support when the shell exposes it", () => {
    window.diariz = { canCaptureScreenshot: true };

    expect(canCaptureScreenshots()).toBe(true);
  });

  it("subscribing without a shell is a no-op that still returns an unsubscribe", () => {
    const unsubscribe = onScreenshotCaptured(() => {});

    expect(() => unsubscribe()).not.toThrow();
  });

  // The shell delivers Node Buffers over Electron's structured-clone IPC, which arrive here as
  // Uint8Array (NOT ArrayBuffer) - see preload.js's onScreenshotCaptured JSDoc.
  it("converts the shell's Uint8Arrays into typed image blobs", async () => {
    let emit: ((payload: unknown) => void) | null = null;
    window.diariz = {
      canCaptureScreenshot: true,
      onScreenshotCaptured: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {};
      },
    };
    const seen: { full: Blob; thumb: Blob; width: number; height: number }[] = [];
    onScreenshotCaptured((shot) => seen.push(shot));

    emit!({ full: new Uint8Array([1, 2]), thumb: new Uint8Array([3]), width: 800, height: 600 });

    expect(seen).toHaveLength(1);
    expect(seen[0].full.type).toBe("image/png");
    expect(seen[0].thumb.type).toBe("image/jpeg");
    expect(seen[0].width).toBe(800);
    expect(seen[0].height).toBe(600);
  });

  it("requesting a capture without a shell does not throw", () => {
    expect(() => requestCapture()).not.toThrow();
    expect(() => requestChangeArea()).not.toThrow();
  });

  it("forwards a capture request to the shell", () => {
    const captureScreenshot = vi.fn();
    const changeCaptureArea = vi.fn();
    window.diariz = { canCaptureScreenshot: true, captureScreenshot, changeCaptureArea };

    requestCapture();
    requestChangeArea();

    expect(captureScreenshot).toHaveBeenCalledOnce();
    expect(changeCaptureArea).toHaveBeenCalledOnce();
  });
});

describe("auto-capture", () => {
  it("does nothing in a plain browser rather than throwing", () => {
    expect(() => requestToggleAutoCapture()).not.toThrow();
  });

  it("asks the shell to toggle auto-capture", () => {
    const toggleAutoCapture = vi.fn();
    window.diariz = { canCaptureScreenshot: true, toggleAutoCapture };

    requestToggleAutoCapture();

    expect(toggleAutoCapture).toHaveBeenCalledTimes(1);
  });

  // Auto-capture stops without the user asking - the recording ends, or the capture area's display is
  // unplugged - so the renderer is told rather than left showing a lit toggle over a dead loop.
  it("relays the shell's start and stop, with the area to capture", () => {
    let emit: ((payload: unknown) => void) | null = null;
    window.diariz = {
      canCaptureScreenshot: true,
      onAutoCaptureChanged: (cb: (payload: unknown) => void) => {
        emit = cb;
        return () => {};
      },
    };
    const seen: unknown[] = [];

    onAutoCaptureChanged((state) => seen.push(state));
    const area = { displayWidth: 1920, displayHeight: 1200, crop: null };
    emit?.({ active: true, area });
    emit?.({ active: false });

    expect(seen).toEqual([{ active: true, area }, { active: false }]);
  });

  // An older shell has no auto-capture bridge at all. The web app must degrade to "this build cannot do
  // it" rather than throwing on a missing method - the desktop app updates on its own schedule, so a new
  // web app against an old shell is a normal state, not an error.
  it("subscribing against an older shell is a no-op that still returns an unsubscribe", () => {
    window.diariz = { canCaptureScreenshot: true };

    const unsubscribe = onAutoCaptureChanged(() => {});

    expect(() => unsubscribe()).not.toThrow();
  });

  it("reports whether this shell can auto-capture at all", () => {
    expect(canAutoCapture()).toBe(false);

    window.diariz = { canCaptureScreenshot: true };
    expect(canAutoCapture()).toBe(false);

    window.diariz = { canCaptureScreenshot: true, toggleAutoCapture: () => {} };
    expect(canAutoCapture()).toBe(true);
  });
});
