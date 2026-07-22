import { afterEach, describe, expect, it, vi } from "vitest";
import {
  canCaptureScreenshots,
  onScreenshotCaptured,
  requestCapture,
  requestChangeArea,
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
