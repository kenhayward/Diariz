import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { startSilenceWatcher } from "./silenceWatcher";

// jsdom has no Web Audio, so the analyser is stubbed. `sample` is the raw byte the analyser reports:
// 128 is the silence midpoint, 200 is comfortably above SILENCE_LEVEL once normalised.
const QUIET = 128;
const LOUD = 200;
let sample = QUIET;

class FakeAudioContext {
  closed = false;
  createMediaStreamSource() {
    return { connect() {}, disconnect() {} };
  }
  createAnalyser() {
    return {
      fftSize: 256,
      smoothingTimeConstant: 0,
      getByteTimeDomainData(buf: Uint8Array) {
        buf.fill(sample);
      },
    };
  }
  close() {
    this.closed = true;
    return Promise.resolve();
  }
}

const stream = {} as MediaStream;

describe("startSilenceWatcher", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    sample = QUIET;
    (window as unknown as { AudioContext?: unknown }).AudioContext = FakeAudioContext;
  });

  afterEach(() => {
    vi.useRealTimers();
    delete (window as unknown as { AudioContext?: unknown }).AudioContext;
  });

  it("reports the live silence run, so a caller can ask whether anyone is talking now", () => {
    // The extend prompt needs the state *between* thresholds - "is anyone talking right now" - not just the
    // one-shot callback at the threshold.
    const watcher = startSilenceWatcher(stream, 30_000, () => {})!;
    expect(watcher).toBeTruthy();

    expect(watcher.state()).toEqual({ heardSound: false, silentMs: 0 });

    sample = LOUD;
    vi.advanceTimersByTime(500);
    expect(watcher.state()).toEqual({ heardSound: true, silentMs: 0 });

    sample = QUIET;
    vi.advanceTimersByTime(2_000);
    expect(watcher.state().silentMs).toBe(2_000);

    sample = LOUD;
    vi.advanceTimersByTime(500);
    expect(watcher.state().silentMs).toBe(0);

    watcher.stop();
  });

  it("still watches with the silence rule turned off, so the extend prompt can still read the room", () => {
    // A non-positive threshold means "never auto-stop", not "stop looking": that user is exactly the one
    // whose extend prompt has no silence floor under it, so the room still has to be observable.
    const onSilent = vi.fn();
    const watcher = startSilenceWatcher(stream, 0, onSilent);
    expect(watcher).not.toBeNull();

    sample = LOUD;
    vi.advanceTimersByTime(500);
    sample = QUIET;
    vi.advanceTimersByTime(10 * 60_000);

    expect(onSilent).not.toHaveBeenCalled();
    expect(watcher!.state().heardSound).toBe(true);
    watcher!.stop();
  });

  it("returns null when Web Audio is unavailable, rather than failing the recording", () => {
    delete (window as unknown as { AudioContext?: unknown }).AudioContext;
    expect(startSilenceWatcher(stream, 30_000, () => {})).toBeNull();
  });
});
