import { describe, it, expect, vi } from "vitest";
import { connectTrayRecorder, type TrayBridge, type TrayCommand } from "./trayRecorder";

/// A fake of the `window.diariz` bridge the Electron preload injects.
function fakeBridge() {
  let handler: ((cmd: TrayCommand) => void) | null = null;
  const reported: unknown[] = [];
  const removed = vi.fn();
  const bridge: TrayBridge = {
    isElectron: true,
    onTrayCommand: (cb) => {
      handler = cb;
      return removed;
    },
    reportRecorderState: (s) => reported.push(s),
  };
  return {
    bridge,
    reported,
    removed,
    dispatch: (cmd: TrayCommand) => handler?.(cmd),
  };
}

describe("connectTrayRecorder", () => {
  it("announces readiness when it connects", () => {
    const f = fakeBridge();
    connectTrayRecorder(f.bridge, { onStart: vi.fn(), onStop: vi.fn() });
    expect(f.reported).toContainEqual({ phase: "idle", ready: true });
  });

  it("routes start commands to onStart with the source", () => {
    const f = fakeBridge();
    const onStart = vi.fn();
    connectTrayRecorder(f.bridge, { onStart, onStop: vi.fn() });
    f.dispatch({ type: "start", source: "system" });
    expect(onStart).toHaveBeenCalledWith("system");
  });

  it("routes stop commands to onStop", () => {
    const f = fakeBridge();
    const onStop = vi.fn();
    connectTrayRecorder(f.bridge, { onStart: vi.fn(), onStop });
    f.dispatch({ type: "stop" });
    expect(onStop).toHaveBeenCalledOnce();
  });

  it("forwards reportState to the bridge", () => {
    const f = fakeBridge();
    const conn = connectTrayRecorder(f.bridge, { onStart: vi.fn(), onStop: vi.fn() });
    conn.reportState({ phase: "recording", source: "mic" });
    expect(f.reported).toContainEqual({ phase: "recording", source: "mic" });
  });

  it("dispose unsubscribes from the bridge", () => {
    const f = fakeBridge();
    const conn = connectTrayRecorder(f.bridge, { onStart: vi.fn(), onStop: vi.fn() });
    conn.dispose();
    expect(f.removed).toHaveBeenCalledOnce();
  });

  it("is a safe no-op in a plain browser (no bridge)", () => {
    const conn = connectTrayRecorder(undefined, { onStart: vi.fn(), onStop: vi.fn() });
    expect(() => conn.reportState({ phase: "idle" })).not.toThrow();
    expect(() => conn.dispose()).not.toThrow();
  });
});
