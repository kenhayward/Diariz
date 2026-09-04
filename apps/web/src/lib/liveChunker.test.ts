import { describe, expect, it } from "vitest";
import {
  DEFAULT_CHUNKER_LIMITS,
  resolveChunkerLimits,
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

/// Durations are expressed against the limits rather than written out, so these read as the rules they
/// are - "a pause past the minimum cuts", "the maximum always cuts" - and stay true when the limits are
/// retuned. The values themselves are pinned once, deliberately, in the block at the end.
const MIN = DEFAULT_CHUNKER_LIMITS.minMs;
const MAX = DEFAULT_CHUNKER_LIMITS.maxMs;
const PAUSE = DEFAULT_CHUNKER_LIMITS.pauseMs;

/// `n` ticks of 100 ms at one level - the granularity the recorder's meter already runs at.
const ticks = (ms: number, level: number, paused = false) =>
  Array.from({ length: Math.round(ms / 100) }, () => ({ dtMs: 100, level, paused }));

describe("liveChunker", () => {
  it("cuts at the first sustained pause once the minimum has elapsed", () => {
    // Speech just past the minimum, then a real pause - the ordinary case, and the one the whole
    // pause-based design exists for. Kept under the maximum so the cut can only be the pause.
    const speech = MIN + 1_000;
    const cuts = run([...ticks(speech, LOUD), ...ticks(PAUSE + 200, QUIET), ...ticks(1_000, LOUD)]);

    expect(cuts).toHaveLength(1);
    // Cut lands where the pause qualified, not where it started.
    expect(cuts[0]).toBeGreaterThanOrEqual(speech + PAUSE);
    expect(cuts[0]).toBeLessThan(speech + PAUSE + 500);
  });

  it("does not cut on a pause before the minimum", () => {
    // Chunks shorter than the minimum give the diarizer too little to cluster - measured: a clip with
    // one speaker costs a fraction of one with two, so short chunks are cheap for the wrong reason.
    // Silence stops well short of the minimum, so nothing qualifies.
    const cuts = run([...ticks(MIN / 2, LOUD), ...ticks(MIN / 4, QUIET), ...ticks(MIN / 4, LOUD)]);
    expect(cuts).toEqual([]);
  });

  it("forces a cut at the maximum even mid-sentence", () => {
    // Somebody monologuing must not produce one unbounded chunk: the whole point is that audio
    // reaches the server while the meeting runs.
    // Just over one maximum of unbroken speech: exactly one forced cut, at the maximum.
    const cuts = run(ticks(MAX + 1_000, LOUD));
    expect(cuts).toHaveLength(1);
    expect(cuts[0]).toBeCloseTo(MAX, -2);
  });

  it("never cuts while paused, however long the silence", () => {
    // A paused recorder is not producing audio, so a cut would emit an empty chunk.
    // Under the maximum, then paused for a long time. Without the paused guard the silence alone
    // would qualify the moment the minimum passed.
    const cuts = run([...ticks(MAX - 1_000, LOUD), ...ticks(120_000, QUIET, true)]);
    expect(cuts).toEqual([]);
  });

  it("measures elapsed on the recorded clock, so a pause does not advance it", () => {
    // A chunker on wall-clock time would fire straight through a long pause and emit nothing but
    // silence. The recorder's clock already excludes paused time; this must use the same one.
    const cuts = run([
      ...ticks(MIN / 2, LOUD),
      ...ticks(300_000, QUIET, true), // five minutes paused
      ...ticks(MIN / 4, LOUD),
      ...ticks(PAUSE + 200, QUIET),
    ]);
    expect(cuts).toEqual([]);
  });

  it("starts the next chunk cleanly after a cut", () => {
    const speech = MIN + 1_000;
    const cuts = run([
      ...ticks(speech, LOUD),
      ...ticks(PAUSE + 200, QUIET),
      ...ticks(speech, LOUD),
      ...ticks(PAUSE + 200, QUIET),
    ]);
    expect(cuts).toHaveLength(2);
    expect(cuts[1] - cuts[0]).toBeGreaterThan(MIN);
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
    // Past the minimum so a real pause WOULD cut here, and under the maximum so nothing is forced.
    const cuts = run([
      ...ticks(MIN + 1_000, LOUD),
      ...ticks(PAUSE - 400, QUIET), // shorter than pauseMs
      ...ticks(1_000, LOUD),
    ]);
    expect(cuts).toEqual([]);
  });

  it("requires real speech before a pause counts", () => {
    // A capture that opens on silence must not immediately emit an empty chunk at the minimum. Run past
    // the MAXIMUM, so this also covers the forced cut: silence alone never produces a chunk.
    const cuts = run(ticks(MAX + 2_000, QUIET));
    expect(cuts).toEqual([]);
  });
});

describe("the default limits", () => {
  it("cuts often enough that the transcript is not a minute behind the room", () => {
    // These are the whole of live latency's dominant term: a word spoken at the start of a chunk cannot
    // leave the browser until that chunk closes, so the maximum IS the worst-case wait before anything
    // is even sent. Everything downstream - upload, queue, GPU, delivery - measured at well under ten
    // seconds combined.
    //
    // Pinned here rather than left implicit because retuning them is a deliberate act with a visible
    // cost on the other side: shorter chunks give the diarizer less to cluster, so speakers churn more
    // early in a meeting and are corrected retroactively more often.
    expect(DEFAULT_CHUNKER_LIMITS.minMs).toBe(6_000);
    expect(DEFAULT_CHUNKER_LIMITS.maxMs).toBe(12_000);
  });

  it("keeps a pause shorter than the gap between sentences and longer than the gap between words", () => {
    expect(DEFAULT_CHUNKER_LIMITS.pauseMs).toBe(700);
  });

  it("leaves room for a pause to qualify inside a chunk", () => {
    // A minimum below the pause window would make the maximum the only thing that ever cuts, and every
    // chunk would end mid-word - which is what the overlap exists to paper over.
    expect(DEFAULT_CHUNKER_LIMITS.minMs).toBeGreaterThan(DEFAULT_CHUNKER_LIMITS.pauseMs * 2);
    expect(DEFAULT_CHUNKER_LIMITS.maxMs).toBeGreaterThan(DEFAULT_CHUNKER_LIMITS.minMs);
  });
});


describe("resolveChunkerLimits", () => {
  const fallback = { minMs: 6_000, maxMs: 12_000, pauseMs: 700 };

  it("takes the server's limits, so they can be retuned without a web deploy", () => {
    expect(resolveChunkerLimits({ minMs: 4_000, maxMs: 9_000, pauseMs: 500 }, fallback))
      .toEqual({ minMs: 4_000, maxMs: 9_000, pauseMs: 500 });
  });

  it("falls back when an older server sends none", () => {
    expect(resolveChunkerLimits(undefined, fallback)).toEqual(fallback);
    expect(resolveChunkerLimits(null, fallback)).toEqual(fallback);
  });

  it("falls back rather than chunking on values that cannot work", () => {
    // A maximum at or below the minimum means the pause rule can never fire before the forced cut, and
    // a non-positive maximum means every tick cuts - which would post a chunk per animation frame. The
    // browser is downstream of a deployment setting somebody can typo, so it checks rather than trusts.
    expect(resolveChunkerLimits({ minMs: 9_000, maxMs: 9_000, pauseMs: 700 }, fallback)).toEqual(fallback);
    expect(resolveChunkerLimits({ minMs: 0, maxMs: 0, pauseMs: 700 }, fallback)).toEqual(fallback);
    expect(resolveChunkerLimits({ minMs: -1, maxMs: 12_000, pauseMs: 700 }, fallback)).toEqual(fallback);
    expect(resolveChunkerLimits({ minMs: 6_000, maxMs: 12_000, pauseMs: 0 }, fallback)).toEqual(fallback);
  });

  it("falls back on anything that is not three numbers", () => {
    expect(resolveChunkerLimits({ minMs: Number.NaN, maxMs: 12_000, pauseMs: 700 }, fallback)).toEqual(fallback);
    expect(resolveChunkerLimits({ minMs: 6_000 } as never, fallback)).toEqual(fallback);
  });

  it("takes all three together or none of them", () => {
    // A half-applied set would be a configuration nobody chose - worse than either end of it.
    const partial = resolveChunkerLimits({ minMs: 3_000, maxMs: 2_000, pauseMs: 400 }, fallback);
    expect(partial).toEqual(fallback);
    expect(partial.pauseMs).toBe(fallback.pauseMs);
  });
});
