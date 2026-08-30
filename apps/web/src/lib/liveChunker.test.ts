import { describe, expect, it } from "vitest";
import {
  DEFAULT_CHUNKER_LIMITS,
  emptyChunkerState,
  shouldCut,
  advance,
  type ChunkerState,
} from "./liveChunker";

/// Feed a sequence of ticks and report where cuts landed, in recorded-clock milliseconds. Reads the
/// way the recorder uses it: advance on every animation frame, cut when it says so.
function run(
  ticks: { dtMs: number; level: number; paused?: boolean }[],
  limits = DEFAULT_CHUNKER_LIMITS,
): number[] {
  let state: ChunkerState = emptyChunkerState();
  const cuts: number[] = [];
  let clock = 0;
  for (const tick of ticks) {
    // The recorded clock is pause-aware: it does not advance while paused.
    if (!tick.paused) clock += tick.dtMs;
    state = advance(state, { dtMs: tick.paused ? 0 : tick.dtMs, level: tick.level, paused: !!tick.paused }, limits);
    if (shouldCut(state, limits)) {
      cuts.push(clock);
      state = emptyChunkerState();
    }
  }
  return cuts;
}

const LOUD = 0.4;
const QUIET = 0.01;

/// `n` ticks of 100 ms at one level - the granularity the recorder's meter already runs at.
const ticks = (ms: number, level: number, paused = false) =>
  Array.from({ length: Math.round(ms / 100) }, () => ({ dtMs: 100, level, paused }));

describe("liveChunker", () => {
  it("cuts at the first sustained pause once the minimum has elapsed", () => {
    const cuts = run([
      ...ticks(22_000, LOUD),
      ...ticks(800, QUIET), // a real pause, past PauseMs
      ...ticks(2_000, LOUD),
    ]);
    expect(cuts).toHaveLength(1);
    // Cut lands where the pause qualified, not where it started.
    expect(cuts[0]).toBeGreaterThanOrEqual(22_000 + DEFAULT_CHUNKER_LIMITS.pauseMs);
    expect(cuts[0]).toBeLessThan(23_000);
  });

  it("does not cut on a pause before the minimum", () => {
    // Chunks shorter than the minimum give the diarizer too little to cluster - measured: a clip with
    // one speaker costs a fraction of one with two, so short chunks are cheap for the wrong reason.
    const cuts = run([...ticks(5_000, LOUD), ...ticks(3_000, QUIET), ...ticks(5_000, LOUD)]);
    expect(cuts).toEqual([]);
  });

  it("forces a cut at the maximum even mid-sentence", () => {
    // Somebody monologuing must not produce one unbounded chunk: the whole point is that audio
    // reaches the server while the meeting runs.
    const cuts = run(ticks(60_000, LOUD));
    expect(cuts).toHaveLength(1);
    expect(cuts[0]).toBeCloseTo(DEFAULT_CHUNKER_LIMITS.maxMs, -2);
  });

  it("never cuts while paused, however long the silence", () => {
    // A paused recorder is not producing audio, so a cut would emit an empty chunk.
    const cuts = run([...ticks(25_000, LOUD), ...ticks(120_000, QUIET, true)]);
    expect(cuts).toEqual([]);
  });

  it("measures elapsed on the recorded clock, so a pause does not advance it", () => {
    // A chunker on wall-clock time would fire straight through a long pause and emit nothing but
    // silence. The recorder's clock already excludes paused time; this must use the same one.
    const cuts = run([
      ...ticks(10_000, LOUD),
      ...ticks(300_000, QUIET, true), // five minutes paused
      ...ticks(5_000, LOUD),
      ...ticks(1_000, QUIET),
    ]);
    expect(cuts).toEqual([]);
  });

  it("starts the next chunk cleanly after a cut", () => {
    const cuts = run([
      ...ticks(21_000, LOUD),
      ...ticks(800, QUIET),
      ...ticks(21_000, LOUD),
      ...ticks(800, QUIET),
    ]);
    expect(cuts).toHaveLength(2);
    expect(cuts[1] - cuts[0]).toBeGreaterThan(DEFAULT_CHUNKER_LIMITS.minMs);
  });

  it("refuses to cut a paused chunk even when it is over the maximum", () => {
    // `advance` freezes the counters while paused, so this state is not reachable by ticking - it
    // needs a caller that let elapsed pass the maximum and only then checked. Asserted directly
    // because it is the one case the paused flag in `shouldCut` actually defends, and a guard no
    // test can reach is a guard that will be deleted by the next person who reads it.
    expect(
      shouldCut(
        { elapsedMs: DEFAULT_CHUNKER_LIMITS.maxMs + 5_000, silentMs: 5_000, heardSpeech: true, paused: true },
        DEFAULT_CHUNKER_LIMITS,
      ),
    ).toBe(false);
    // The same state unpaused does cut, so the assertion above is about the flag and nothing else.
    expect(
      shouldCut(
        { elapsedMs: DEFAULT_CHUNKER_LIMITS.maxMs + 5_000, silentMs: 5_000, heardSpeech: true, paused: false },
        DEFAULT_CHUNKER_LIMITS,
      ),
    ).toBe(true);
  });

  it("treats a brief dip as speech, not a boundary", () => {
    // One quiet frame between words is not a pause. Cutting there would slice mid-sentence, which is
    // exactly the seam error overlapping decode windows exist to clean up.
    const cuts = run([
      ...ticks(25_000, LOUD),
      ...ticks(200, QUIET), // shorter than pauseMs
      ...ticks(5_000, LOUD),
    ]);
    expect(cuts).toEqual([]);
  });

  it("requires real speech before a pause counts", () => {
    // A capture that opens on silence must not immediately emit an empty chunk at the minimum.
    const cuts = run([...ticks(25_000, QUIET), ...ticks(2_000, QUIET)]);
    expect(cuts).toEqual([]);
  });
});
