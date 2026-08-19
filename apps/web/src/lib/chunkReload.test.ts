import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  handlePreloadError,
  installChunkReloadHandler,
  isStaleChunkError,
  setCapturing,
  type ChunkReloadDeps,
} from "./chunkReload";

/// A sessionStorage stand-in. The real guard has to survive the reload it triggers, which is exactly what
/// sessionStorage does and a plain variable would not - so the seam is the storage object, not a boolean.
function fakeStorage(initial: Record<string, string> = {}) {
  const map = new Map(Object.entries(initial));
  return {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
    read: (k: string) => map.get(k) ?? null,
  };
}

function deps(over: Partial<ChunkReloadDeps> & { at?: number } = {}) {
  const storage = over.storage ?? fakeStorage();
  const reload = over.reload ?? vi.fn();
  return {
    reload,
    storage,
    isCapturing: over.isCapturing ?? (() => false),
    now: over.now ?? (() => over.at ?? 1_000_000),
  } as ChunkReloadDeps & { reload: ReturnType<typeof vi.fn> };
}

describe("chunkReload", () => {
  beforeEach(() => setCapturing(false));

  describe("handlePreloadError", () => {
    it("reloads when a chunk goes missing, which is the whole recovery", () => {
      const d = deps();
      expect(handlePreloadError(d)).toBe(true);
      expect(d.reload).toHaveBeenCalledTimes(1);
    });

    // The renderer is where MediaRecorder lives, so a reload tears down an in-progress take. The error is
    // left to surface instead, and the boundary offers the reload for the user to take when they are ready.
    it("does not reload while a capture is in flight", () => {
      const d = deps({ isCapturing: () => true });
      expect(handlePreloadError(d)).toBe(false);
      expect(d.reload).not.toHaveBeenCalled();
    });

    // The loop this guards is real: if the failing chunk belongs to the route the user is ALREADY on, the
    // reload re-runs the same failing import, which would reload again, forever.
    it("does not reload twice in quick succession", () => {
      const storage = fakeStorage();
      const first = deps({ storage, now: () => 1_000_000 });
      handlePreloadError(first);

      const second = deps({ storage, now: () => 1_000_000 + 3_000 });
      expect(handlePreloadError(second)).toBe(false);
      expect(second.reload).not.toHaveBeenCalled();
    });

    // But the guard must not be permanent: the desktop shell hides to tray and can run across many deploys,
    // and each one should still recover on its own.
    it("reloads again once the loop window has passed, so a later deploy still recovers", () => {
      const storage = fakeStorage();
      handlePreloadError(deps({ storage, now: () => 1_000_000 }));

      const later = deps({ storage, now: () => 1_000_000 + 60_000 });
      expect(handlePreloadError(later)).toBe(true);
      expect(later.reload).toHaveBeenCalledTimes(1);
    });
  });

  describe("isStaleChunkError", () => {
    // The exact string the deployed bundle produced, from the reported bug.
    it("recognises the real Vite failure", () => {
      const real = new Error(
        "Failed to fetch dynamically imported module: https://diariz.stocks-hayward.com/assets/LlmModels-CcQYuFEJ.js",
      );
      expect(isStaleChunkError(real)).toBe(true);
    });

    it("recognises the other engines' wording", () => {
      expect(isStaleChunkError(new Error("error loading dynamically imported module"))).toBe(true);
      expect(isStaleChunkError(new Error("Importing a module script failed."))).toBe(true);
      expect(isStaleChunkError(new Error("Unable to preload CSS for /assets/x.css"))).toBe(true);
    });

    // Must not turn every render crash into "a new version is available", which would be a lie and would
    // hide real bugs behind a Reload button.
    it("does not claim an ordinary render crash is a stale chunk", () => {
      expect(isStaleChunkError(new Error("Cannot read properties of undefined (reading 'length')"))).toBe(false);
      expect(isStaleChunkError(undefined)).toBe(false);
    });
  });

  describe("installChunkReloadHandler", () => {
    it("cancels the event when it reloads, so Vite does not rethrow into the boundary", () => {
      const reload = vi.fn();
      const off = installChunkReloadHandler({ ...deps({ reload }), reload });

      const event = new Event("vite:preloadError", { cancelable: true });
      window.dispatchEvent(event);

      expect(reload).toHaveBeenCalledTimes(1);
      expect(event.defaultPrevented).toBe(true);
      off();
    });

    // When it declines to reload, the error MUST still reach the boundary - otherwise the user is left
    // looking at a page that silently did nothing.
    it("lets the event through when it declines to reload", () => {
      const reload = vi.fn();
      const off = installChunkReloadHandler({ ...deps({ reload, isCapturing: () => true }), reload });

      const event = new Event("vite:preloadError", { cancelable: true });
      window.dispatchEvent(event);

      expect(reload).not.toHaveBeenCalled();
      expect(event.defaultPrevented).toBe(false);
      off();
    });

    it("stops listening once removed", () => {
      const reload = vi.fn();
      const off = installChunkReloadHandler({ ...deps({ reload }), reload });
      off();

      window.dispatchEvent(new Event("vite:preloadError", { cancelable: true }));

      expect(reload).not.toHaveBeenCalled();
    });
  });

  describe("setCapturing", () => {
    it("is what the default isCapturing reads, so the Recorder can veto a reload", () => {
      const reload = vi.fn();
      const off = installChunkReloadHandler({ reload, storage: fakeStorage(), now: () => 1_000_000 });

      setCapturing(true);
      window.dispatchEvent(new Event("vite:preloadError", { cancelable: true }));
      expect(reload).not.toHaveBeenCalled();

      setCapturing(false);
      window.dispatchEvent(new Event("vite:preloadError", { cancelable: true }));
      expect(reload).toHaveBeenCalledTimes(1);
      off();
    });
  });
});
