import { describe, expect, it, vi } from "vitest";
import { createSlideCapture, type CommittedFrame, type SlideCaptureFrames } from "./slideCapture";
import { dhash, HASH_SIZE } from "./slideDetector";

const W = HASH_SIZE + 1;
const H = HASH_SIZE;

type Pattern = (x: number) => number;

/// RGBA sample bytes from a grey-value function - the layout a canvas ImageData hands over.
function sample(valueAt: Pattern): Uint8ClampedArray {
  const buf = new Uint8ClampedArray(W * H * 4);
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const v = valueAt(x);
      const i = (y * W + x) * 4;
      buf[i] = v;
      buf[i + 1] = v;
      buf[i + 2] = v;
      buf[i + 3] = 255;
    }
  }
  return buf;
}

const bar = (from: number, to: number): Pattern => (x) => (x >= from && x < to ? 240 : 20);
const SLIDE_A = bar(2, 6);
const SLIDE_B = bar(10, 14);
const SLIDE_C = bar(6, 10);

const blob = (tag: string) => new Blob([tag], { type: "image/png" });

/**
 * A frame source under the test's control. The real one owns a getDisplayMedia stream and two canvases;
 * this one just replays whatever pattern is currently "on screen", which is what lets the whole
 * orchestration - stamping, dedupe, the cap, failure handling - be tested without a browser.
 */
function fakeFrames(initial: Pattern) {
  let onScreen = initial;
  // What `commit()` will actually return, when it should differ from what `sample()` last reported -
  // the commit-time race, where the screen moves on between the decision and the grab.
  let commitsAs: Pattern | null = null;
  let failures = 0;
  const frames: SlideCaptureFrames & {
    show(p: Pattern): void;
    commitAs(p: Pattern | null): void;
    failNext(n: number): void;
    closed: boolean;
    commits: number;
  } = {
    closed: false,
    commits: 0,
    show(p) {
      onScreen = p;
    },
    commitAs(p) {
      commitsAs = p;
    },
    failNext(n) {
      failures = n;
    },
    sample: () => sample(onScreen),
    async commit(): Promise<CommittedFrame | null> {
      frames.commits++;
      if (failures > 0) {
        failures--;
        return null;
      }
      const shown = commitsAs ?? onScreen;
      return {
        full: blob("full"),
        thumb: blob("thumb"),
        width: 1920,
        height: 1200,
        hash: dhash(sample(shown), W, H, { pixelOrder: "rgba" }),
      };
    },
    close() {
      frames.closed = true;
    },
  };
  return frames;
}

interface Harness {
  frames: ReturnType<typeof fakeFrames>;
  captures: { capturedAtMs: number; width: number; height: number }[];
  stopped: string[];
  tick: (atMs: number) => Promise<void>;
  capture: ReturnType<typeof createSlideCapture>;
}

function harness(overrides: { maxCaptures?: number; initial?: Pattern; suspended?: () => boolean } = {}): Harness {
  const frames = fakeFrames(overrides.initial ?? SLIDE_A);
  const captures: Harness["captures"] = [];
  const stopped: string[] = [];
  let clock = 0;

  const capture = createSlideCapture({
    frames,
    nowMs: () => clock,
    isSuspended: overrides.suspended,
    maxCaptures: overrides.maxCaptures ?? 200,
    onCapture: (shot) => captures.push({ capturedAtMs: shot.capturedAtMs, width: shot.width, height: shot.height }),
    onStopped: (reason) => stopped.push(reason),
  });

  return {
    frames,
    captures,
    stopped,
    capture,
    tick: async (atMs: number) => {
      clock = atMs;
      await capture.tick();
    },
  };
}

/// Sample once a second for `seconds`, which is what the real ticker does.
async function run(h: Harness, seconds: number, startAt = 0) {
  for (let i = 0; i < seconds; i++) await h.tick((startAt + i) * 1000);
}

describe("slideCapture", () => {
  it("captures a slide once it has settled, not once per tick", async () => {
    const h = harness();

    await run(h, 10);

    expect(h.captures).toHaveLength(1);
  });

  // The whole reason detection runs in the renderer: it owns the pause-aware recording clock, so the
  // capture can be stamped with the moment the slide APPEARED rather than the moment it was confirmed.
  // Stamping at confirmation would file every slide `stableSamples` ticks late - enough to place it
  // after the sentence that introduced it.
  it("stamps a capture with when the slide appeared, not when it was confirmed", async () => {
    const h = harness();

    await run(h, 4);
    h.frames.show(SLIDE_B);
    await run(h, 5, 4);

    expect(h.captures).toHaveLength(2);
    expect(h.captures[1].capturedAtMs).toBe(4000);
  });

  it("passes the captured image's real dimensions through", async () => {
    const h = harness();

    await run(h, 5);

    expect(h.captures[0]).toMatchObject({ width: 1920, height: 1200 });
  });

  it("does not capture a slide the presenter has already shown", async () => {
    const h = harness();

    await run(h, 4);
    h.frames.show(SLIDE_B);
    await run(h, 4, 4);
    h.frames.show(SLIDE_A); // back two slides
    await run(h, 4, 8);

    expect(h.captures).toHaveLength(2);
  });

  it("discards a capture whose screen changed during the grab", async () => {
    const h = harness();
    h.frames.commitAs(SLIDE_C); // the grab always catches a different screen

    await run(h, 6);

    expect(h.frames.commits).toBeGreaterThan(0);
    expect(h.captures).toHaveLength(0);
  });

  describe("when the frames stop coming", () => {
    it("keeps going after a single failed grab", async () => {
      const h = harness();
      h.frames.failNext(1);

      await run(h, 10);

      expect(h.captures).toHaveLength(1);
      expect(h.stopped).toEqual([]);
    });

    it("gives up after repeated failures rather than looping on a dead source", async () => {
      const h = harness();
      h.frames.failNext(99);

      await run(h, 12);

      expect(h.stopped).toEqual(["frames-failed"]);
      expect(h.frames.closed).toBe(true);
    });

    it("skips a tick with no frame available yet, without counting it as a failure", async () => {
      const h = harness();
      h.frames.sample = () => null;

      await run(h, 10);

      expect(h.stopped).toEqual([]);
      expect(h.frames.commits).toBe(0);
    });
  });

  // Left alone, auto-capture would keep firing for the rest of the meeting once the recording hit its
  // screenshot limit - dropping every capture while the toggle stayed lit, which reads as working.
  describe("the capture limit", () => {
    it("stops itself once the recording is full", async () => {
      const h = harness({ maxCaptures: 2 });

      await run(h, 4);
      h.frames.show(SLIDE_B);
      await run(h, 4, 4);
      h.frames.show(SLIDE_C);
      await run(h, 4, 8);

      expect(h.captures).toHaveLength(2);
      expect(h.stopped).toEqual(["cap-reached"]);
    });

    it("releases the screen capture when it stops", async () => {
      const h = harness({ maxCaptures: 1 });

      await run(h, 4);
      h.frames.show(SLIDE_B);
      await run(h, 4, 4);

      expect(h.frames.closed).toBe(true);
    });

    it("reports stopping once, not on every later tick", async () => {
      const h = harness({ maxCaptures: 1 });

      await run(h, 20);
      h.frames.show(SLIDE_B);
      await run(h, 20, 20);

      expect(h.stopped).toEqual(["cap-reached"]);
    });
  });

  describe("stopping", () => {
    it("releases the screen capture", () => {
      const h = harness();

      h.capture.stop();

      expect(h.frames.closed).toBe(true);
    });

    it("captures nothing more once stopped", async () => {
      const h = harness();

      h.capture.stop();
      await run(h, 10);

      expect(h.captures).toHaveLength(0);
    });

    it("is safe to call twice", () => {
      const h = harness();

      h.capture.stop();
      h.capture.stop();

      expect(h.frames.closed).toBe(true);
    });
  });

  describe("ticking", () => {
    it("samples on the interval once started, and stops when stopped", async () => {
      vi.useFakeTimers();
      try {
        const frames = fakeFrames(SLIDE_A);
        const spy = vi.spyOn(frames, "sample");
        const capture = createSlideCapture({
          frames,
          nowMs: () => 0,
          maxCaptures: 200,
          sampleIntervalMs: 1000,
          onCapture: () => {},
          onStopped: () => {},
        });

        capture.start();
        await vi.advanceTimersByTimeAsync(3000);
        const whileRunning = spy.mock.calls.length;
        capture.stop();
        await vi.advanceTimersByTimeAsync(5000);

        expect(whileRunning).toBe(3);
        expect(spy.mock.calls.length).toBe(whileRunning);
      } finally {
        vi.useRealTimers();
      }
    });

    // A tick that is still grabbing must not have another stacked on top of it: the grab is far slower
    // than the interval on a busy machine, and overlapping grabs would fight over the same frame source.
    it("does not start a tick while the previous one is still running", async () => {
      const frames = fakeFrames(SLIDE_A);
      let release: (() => void) | null = null;
      frames.commit = () =>
        new Promise((resolve) => {
          release = () => resolve(null);
        });
      const capture = createSlideCapture({
        frames,
        nowMs: () => 0,
        maxCaptures: 200,
        onCapture: () => {},
        onStopped: () => {},
      });
      const sampleSpy = vi.spyOn(frames, "sample");

      // Three ticks settle a slide and start a grab that never resolves; further ticks must no-op.
      const inFlight = [capture.tick(), capture.tick(), capture.tick()];
      await Promise.resolve();
      const before = sampleSpy.mock.calls.length;
      await capture.tick();
      await capture.tick();

      expect(sampleSpy.mock.calls.length).toBe(before);
      release?.();
      await Promise.all(inFlight);
    });
  });
});

// Pausing a recording freezes its clock. Left running, auto-capture would keep filing slides at an
// offset that never advances - stacking every capture taken during the pause onto one moment in the
// transcript - and would go on capturing a screen the user has stepped away from.
describe("while the recording is paused", () => {
  it("captures nothing", async () => {
    let paused = true;
    const h = harness({ suspended: () => paused });

    await run(h, 10);

    expect(h.captures).toHaveLength(0);
    void paused;
  });

  it("does not even sample, so a paused meeting costs nothing", async () => {
    const h = harness({ suspended: () => true });
    const spy = vi.spyOn(h.frames, "sample");

    await run(h, 5);

    expect(spy).not.toHaveBeenCalled();
  });

  it("picks up again on resume", async () => {
    let paused = true;
    const h = harness({ suspended: () => paused });

    await run(h, 5);
    paused = false;
    await run(h, 5, 5);

    expect(h.captures).toHaveLength(1);
  });

  // The stream stays open across a pause rather than being torn down and re-granted: re-opening would
  // need a fresh getDisplayMedia grant, and it is held for 0.1% of a core.
  it("keeps the screen capture open, ready to resume", async () => {
    const h = harness({ suspended: () => true });

    await run(h, 5);

    expect(h.frames.closed).toBe(false);
  });

  // A slide that went up before the pause and is still there after it is the same slide. Resuming must
  // not re-file it just because the loop stopped watching for a while.
  it("does not re-capture the slide that was already on screen", async () => {
    let paused = false;
    const h = harness({ suspended: () => paused });

    await run(h, 5);
    paused = true;
    await run(h, 10, 5);
    paused = false;
    await run(h, 5, 15);

    expect(h.captures).toHaveLength(1);
  });
});
