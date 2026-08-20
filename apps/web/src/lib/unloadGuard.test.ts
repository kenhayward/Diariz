import { describe, it, expect } from "vitest";
import { installUnloadGuard, shouldBlockUnload } from "./unloadGuard";

/// Dispatches the real event and reports whether the guard cancelled it. `beforeunload` is cancelled by
/// preventDefault, which is what makes the browser show its "Leave site?" prompt - so defaultPrevented is
/// the observable behaviour, not an implementation detail.
function dispatchBeforeUnload(): boolean {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

describe("unloadGuard", () => {
  describe("shouldBlockUnload", () => {
    it("blocks while a capture is in flight", () => {
      expect(shouldBlockUnload({ isCapturing: () => true, isDesktop: () => false })).toBe(true);
    });

    it("does not block when nothing is being captured", () => {
      expect(shouldBlockUnload({ isCapturing: () => false, isDesktop: () => false })).toBe(false);
    });

    // The trap this exists to avoid: Electron cancels a window close when beforeunload is handled, but shows
    // NO dialog. Guarding there would make the tray's Quit silently do nothing, which reads as a broken app.
    // The desktop confirms in its main process instead, where it can actually ask.
    it("never blocks in the desktop app, which cancels the close without asking", () => {
      expect(shouldBlockUnload({ isCapturing: () => true, isDesktop: () => true })).toBe(false);
    });
  });

  describe("installUnloadGuard", () => {
    it("cancels the unload while capturing, so the browser asks before leaving", () => {
      const off = installUnloadGuard({ isCapturing: () => true, isDesktop: () => false });
      expect(dispatchBeforeUnload()).toBe(true);
      off();
    });

    it("lets the unload through when idle, so ordinary navigation is never interrupted", () => {
      const off = installUnloadGuard({ isCapturing: () => false, isDesktop: () => false });
      expect(dispatchBeforeUnload()).toBe(false);
      off();
    });

    it("reads the capture state at unload time, not at install time", () => {
      let capturing = false;
      const off = installUnloadGuard({ isCapturing: () => capturing, isDesktop: () => false });

      expect(dispatchBeforeUnload()).toBe(false);
      capturing = true;
      expect(dispatchBeforeUnload()).toBe(true);
      off();
    });

    it("stops guarding once removed", () => {
      const off = installUnloadGuard({ isCapturing: () => true, isDesktop: () => false });
      off();
      expect(dispatchBeforeUnload()).toBe(false);
    });
  });
});
