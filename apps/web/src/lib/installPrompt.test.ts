/**
 * The module keeps state at module scope (see installPrompt.ts for why), so each test re-imports it
 * fresh via vi.resetModules() rather than sharing one instance across the file.
 */
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act } from "@testing-library/react";

/// A stand-in for Chromium's BeforeInstallPromptEvent: a real Event (so dispatchEvent works) carrying
/// the one method the real one adds.
function installEvent(): Event & { prompt: ReturnType<typeof vi.fn> } {
  const e = new Event("beforeinstallprompt") as Event & { prompt: ReturnType<typeof vi.fn> };
  e.prompt = vi.fn().mockResolvedValue(undefined);
  return e;
}

/// jsdom has no matchMedia at all (see theme.test.tsx), so the module's installed-check needs one
/// stubbed in before it is imported.
function stubDisplayMode(standalone: boolean) {
  window.matchMedia = vi.fn().mockReturnValue({
    matches: standalone,
    addEventListener: () => {},
    removeEventListener: () => {},
  }) as unknown as typeof window.matchMedia;
}

async function load() {
  vi.resetModules();
  return await import("./installPrompt");
}

beforeEach(() => {
  stubDisplayMode(false);
  vi.doUnmock("./audioSource");
});

afterEach(() => {
  vi.resetModules();
});

describe("useInstallPrompt", () => {
  it("cannot install before the browser has offered", async () => {
    // No event means one of: wrong browser, plain http, criteria unmet, or already installed. All of
    // them are "do not show the row".
    const { useInstallPrompt } = await load();
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });

  it("can install once the browser has offered, even if the event fired before React mounted", async () => {
    // The load-order case the module exists to handle: dispatch first, mount second.
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(true);
  });

  it("can install when the event arrives after mounting", async () => {
    const { useInstallPrompt } = await load();
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
    act(() => {
      window.dispatchEvent(installEvent());
    });
    expect(result.current.canInstall).toBe(true);
  });

  it("suppresses Chromium's own mini-infobar, so the menu row is the only affordance", async () => {
    await load();
    const e = installEvent();
    const prevented = vi.spyOn(e, "preventDefault");
    act(() => {
      window.dispatchEvent(e);
    });
    expect(prevented).toHaveBeenCalled();
  });

  it("prompts the stashed event, once", async () => {
    const { useInstallPrompt } = await load();
    const e = installEvent();
    act(() => {
      window.dispatchEvent(e);
    });
    const { result } = renderHook(() => useInstallPrompt());
    act(() => {
      result.current.install();
    });
    expect(e.prompt).toHaveBeenCalledTimes(1);
    // The event is single-use - Chromium rejects a second prompt() on the same one - so the row must go
    // away rather than sit there doing nothing.
    expect(result.current.canInstall).toBe(false);
    act(() => {
      result.current.install();
    });
    expect(e.prompt).toHaveBeenCalledTimes(1);
  });

  it("stops offering once the app has been installed", async () => {
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(true);
    act(() => {
      window.dispatchEvent(new Event("appinstalled"));
    });
    expect(result.current.canInstall).toBe(false);
  });

  it("never offers inside an installed window", async () => {
    // Offering to install the app you are already running is nonsense, and Chromium can still fire the
    // event in some launch paths.
    stubDisplayMode(true);
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });

  it("never offers inside the Electron shell, which is already the desktop app", async () => {
    vi.doMock("./audioSource", () => ({ isElectron: true }));
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });
});
