import { render } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import HubLevelMeter from "./HubLevelMeter";

// ---- Fake Web Audio + rAF, driven frame-by-frame from the test ----
// Mirrors InputLevelMeter.test.tsx: silence detection is read from the time-domain buffer, while the
// 5 bars are driven from the frequency buffer (which we leave at zeros - the bar heights aren't asserted).

const silent = new Uint8Array(256).fill(128); // 128 = time-domain silence
const loud = Uint8Array.from({ length: 256 }, (_, i) => (i % 2 === 0 ? 255 : 0)); // full-scale square
let currentSample: Uint8Array = silent;

const closeSpy = vi.fn();
const ctorSpy = vi.fn();

class FakeAnalyser {
  fftSize = 2048;
  smoothingTimeConstant = 0;
  get frequencyBinCount() {
    return this.fftSize / 2;
  }
  connect() {}
  disconnect() {}
  getByteFrequencyData(buf: Uint8Array) {
    buf.fill(0); // bars stay flat - not asserted here
  }
  getByteTimeDomainData(buf: Uint8Array) {
    buf.set(currentSample.subarray(0, buf.length));
  }
}
class FakeSource {
  connect() {}
  disconnect() {}
}
class FakeAudioContext {
  constructor() {
    ctorSpy();
  }
  createMediaStreamSource() {
    return new FakeSource();
  }
  createAnalyser() {
    return new FakeAnalyser();
  }
  close() {
    closeSpy();
    return Promise.resolve();
  }
}

let rafCb: FrameRequestCallback | null = null;
const cancelSpy = vi.fn();

function step(t: number) {
  const cb = rafCb;
  rafCb = null;
  cb?.(t);
}

const fakeStream = {} as unknown as MediaStream;

beforeEach(() => {
  currentSample = silent;
  rafCb = null;
  vi.clearAllMocks();
  vi.stubGlobal("AudioContext", FakeAudioContext);
  vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => {
    rafCb = cb;
    return 1;
  });
  vi.stubGlobal("cancelAnimationFrame", cancelSpy);
});

afterEach(() => vi.unstubAllGlobals());

describe("HubLevelMeter", () => {
  it("renders nothing and touches no audio API when there is no stream", () => {
    const { container } = render(<HubLevelMeter stream={null} />);
    expect(container.firstChild).toBeNull();
    expect(ctorSpy).not.toHaveBeenCalled();
  });

  it("renders 5 bars and wires an analyser from the given stream", () => {
    const { container } = render(<HubLevelMeter stream={fakeStream} />);
    expect(container.querySelectorAll('[data-testid="hub-meter-bar"]').length).toBe(5);
    expect(ctorSpy).toHaveBeenCalled();
  });

  it("reports silence after ~15s of near-silent input, then clears when sound returns", () => {
    const onSilentChange = vi.fn();
    render(<HubLevelMeter stream={fakeStream} onSilentChange={onSilentChange} />);

    // Two silent frames 16s apart → crosses the 15s hint threshold.
    step(0);
    step(16_000);
    expect(onSilentChange).toHaveBeenLastCalledWith(true);

    // A loud frame resets the silence timer immediately.
    currentSample = loud;
    step(16_250);
    expect(onSilentChange).toHaveBeenLastCalledWith(false);
  });

  it("cleans up the audio graph on unmount", () => {
    const { unmount } = render(<HubLevelMeter stream={fakeStream} />);
    step(0);
    unmount();
    expect(cancelSpy).toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalled();
  });
});
