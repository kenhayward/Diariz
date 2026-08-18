import { describe, expect, it } from "vitest";
import {
  dhash,
  hamming,
  createDetector,
  HASH_SIZE,
  type SlideCandidate,
  type SlideDetectorConfig,
} from "./slideDetector";

// ---- Fixtures -------------------------------------------------------------------------------------
//
// Synthetic frames, built here rather than committed as images or video. The detector only ever sees a
// (HASH_SIZE + 1) x HASH_SIZE grayscale sample, so a fixture is just a function of (x, y) -> 0..255 -
// which makes every scenario below readable as the thing it models rather than as an opaque binary.

const W = HASH_SIZE + 1;
const H = HASH_SIZE;

/// A frame is a function of position to grey value, which keeps every fixture below readable as the
/// thing it models.
type Pattern = (x: number, y: number) => number;

/// BGRA image data from a grey-value function, matching the layout `dhash` reads by default.
function frame(valueAt: Pattern): Uint8ClampedArray {
  const buf = new Uint8ClampedArray(W * H * 4);
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const v = Math.max(0, Math.min(255, Math.round(valueAt(x, y))));
      const i = (y * W + x) * 4;
      buf[i] = v; // B
      buf[i + 1] = v; // G
      buf[i + 2] = v; // R
      buf[i + 3] = 255; // A
    }
  }
  return buf;
}

const hashOf = (valueAt: Pattern) => dhash(frame(valueAt), W, H);

// dHash compares horizontally adjacent pixels, so it encodes *structure*, not brightness: two different
// flat fills hash identically. Every fixture therefore carries vertical edges.
const bar = (from: number, to: number): Pattern => (x) => (x >= from && x < to ? 240 : 20);
const stripes = (period: number): Pattern => (x) => (Math.floor(x / period) % 2 === 0 ? 240 : 20);

const SLIDE_A = bar(2, 6);
const SLIDE_B = bar(10, 14);
const SLIDE_C = stripes(2);

/// SLIDE_A with a couple of pixels disturbed - a mouse cursor, a text caret, codec noise. Structurally
/// the same slide, and the detector must treat it as such.
const SLIDE_A_WITH_CURSOR: Pattern = (x, y) => (x === 8 && y === 7 ? 200 : SLIDE_A(x, y));

/// Cross-fade between two patterns; t = 0 is `from`, t = 1 is `to`.
const blend = (from: Pattern, to: Pattern, t: number): Pattern => (x, y) =>
  from(x, y) * (1 - t) + to(x, y) * t;

// Defaults mirror the shipped ones so the scenarios below exercise the real configuration.
const detector = (overrides?: Partial<SlideDetectorConfig>) => createDetector(overrides);

/// Drive a detector through a list of frames at one sample per second, committing each candidate whose
/// full-resolution confirmation is the same content (the normal case). Returns the committed descriptors.
interface Captured {
  firstSeenAtMs: number;
  hash: Uint8Array;
  duplicate: boolean;
}

function run(
  det: ReturnType<typeof createDetector>,
  patterns: Pattern[],
  { confirmWith = (p: Pattern) => p }: { confirmWith?: (p: Pattern) => Pattern } = {},
): Captured[] {
  const committed: Captured[] = [];
  patterns.forEach((pattern, i) => {
    const commit = det.observe(hashOf(pattern), i * 1000);
    if (!commit) return;
    const outcome = det.confirm(hashOf(confirmWith(pattern)));
    if (outcome.accepted) committed.push({ ...commit, duplicate: outcome.duplicate });
  });
  return committed;
}

const repeat = (pattern: Pattern, n: number): Pattern[] => Array.from({ length: n }, () => pattern);

// ---- dhash ----------------------------------------------------------------------------------------

it("dhash produces a 256-bit digest for the 17x16 sample", () => {
  expect(hashOf(SLIDE_A).length).toBe(32);
});

it("dhash is stable: the same frame always hashes the same", () => {
  expect(hashOf(SLIDE_A)).toEqual(hashOf(SLIDE_A));
});

it("dhash encodes structure, not brightness - so it survives a dimmed projector", () => {
  const dimmed = (x, y) => SLIDE_A(x, y) * 0.6;

  expect(hamming(hashOf(SLIDE_A), hashOf(dimmed)) === 0);
});

it("dhash separates two genuinely different slides", () => {
  expect(hamming(hashOf(SLIDE_A), hashOf(SLIDE_B))).toBeGreaterThan(24);
});

it("dhash barely moves for a cursor drifting over a static slide", () => {
  expect(hamming(hashOf(SLIDE_A), hashOf(SLIDE_A_WITH_CURSOR))).toBeLessThanOrEqual(2);
});

// Frames reach this module from two different worlds with two different byte orders: Electron's
// nativeImage hands back BGRA, and a canvas ImageData (which is where frames come from once they are
// sampled off a getDisplayMedia stream) is RGBA. Reading one as the other silently applies the luma
// weights to the wrong channels - the hash still works, but it weighs colours wrongly, so a red-on-black
// chart and a blue-on-black one become far more alike than they should be.
it("dhash reads a colour frame identically whether the bytes are BGRA or RGBA", () => {
  // Strongly coloured, so a channel mix-up cannot hide: red bars on a blue field.
  const colourAt = (x) => (x >= 2 && x < 6 ? [220, 30, 30] : [20, 20, 200]);
  const pack = (order: "bgra" | "rgba") => {
    const buf = new Uint8ClampedArray(W * H * 4);
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const [r, g, b] = colourAt(x);
        const i = (y * W + x) * 4;
        const [c0, c1, c2] = order === "rgba" ? [r, g, b] : [b, g, r];
        buf[i] = c0;
        buf[i + 1] = c1;
        buf[i + 2] = c2;
        buf[i + 3] = 255;
      }
    }
    return buf;
  };

  const fromNativeImage = dhash(pack("bgra"), W, H, { pixelOrder: "bgra" });
  const fromCanvas = dhash(pack("rgba"), W, H, { pixelOrder: "rgba" });

  expect(hamming(fromNativeImage, fromCanvas)).toBe(0);
});

it("dhash defaults to BGRA, the layout Electron's nativeImage produces", () => {
  const bytes = frame(SLIDE_A);

  expect(dhash(bytes, W, H)).toEqual(dhash(bytes, W, H, { pixelOrder: "bgra" }));
});

// ---- hamming --------------------------------------------------------------------------------------

it("hamming is zero for identical digests", () => {
  expect(hamming(hashOf(SLIDE_A), hashOf(SLIDE_A))).toBe(0);
});

it("hamming is symmetric", () => {
  const [a, b] = [hashOf(SLIDE_A), hashOf(SLIDE_C)];

  expect(hamming(a, b)).toBe(hamming(b, a));
});

it("hamming counts every differing bit, up to the full digest width", () => {
  const zeros = new Uint8Array(32).fill(0x00);
  const ones = new Uint8Array(32).fill(0xff);

  expect(hamming(zeros, ones)).toBe(256);
});

// ---- Detection ------------------------------------------------------------------------------------
//
// Each scenario asserts an EXACT commit count. These are the regression tests that matter: the
// thresholds in config are the brief's starting points and will move once calibrated against a real
// deck, and an assertion on "how many slides came out of this footage" survives that tuning where an
// assertion on a specific hamming distance would not.

it("a slide held on screen is captured once, not once per second", () => {
  const committed = run(detector(), repeat(SLIDE_A, 30));

  expect(committed.length).toBe(1);
});

it("a cursor moving over a static slide does not make it a new slide", () => {
  const patterns = [
    ...repeat(SLIDE_A, 4),
    SLIDE_A_WITH_CURSOR,
    SLIDE_A,
    SLIDE_A_WITH_CURSOR,
    ...repeat(SLIDE_A, 4),
  ];

  const committed = run(detector(), patterns);

  expect(committed.length).toBe(1);
});

it("advancing the deck captures the new slide", () => {
  const committed = run(detector(), [...repeat(SLIDE_A, 5), ...repeat(SLIDE_B, 5)]);

  expect(committed.length).toBe(2);
});

// The whole point of carrying the candidate's first-seen time: the settled image is the right picture,
// but the moment the slide went up is the right timestamp. Committing at the confirmation moment would
// file every slide `stableSamples` seconds late - enough to place it after the sentence introducing it.
it("a slide is timestamped when it appeared, not when it was confirmed", () => {
  const committed = run(detector({ stableSamples: 3 }), [...repeat(SLIDE_A, 5), ...repeat(SLIDE_B, 5)]);

  // SLIDE_B first appears on the sample at t=5s; confirmation needs three steady samples, landing at 7s.
  expect(committed[1].firstSeenAtMs).toBe(5000);
});

it("a transient overlay that comes and goes is not a slide", () => {
  const patterns = [...repeat(SLIDE_A, 4), SLIDE_C, ...repeat(SLIDE_A, 6)];

  const committed = run(detector(), patterns);

  expect(committed.length).toBe(1);
});

const CROSS_FADE = [
  ...repeat(SLIDE_A, 4),
  blend(SLIDE_A, SLIDE_C, 0.25),
  blend(SLIDE_A, SLIDE_C, 0.5),
  blend(SLIDE_A, SLIDE_C, 0.75),
  ...repeat(SLIDE_C, 6),
];

it("an animated build is captured once it settles, not mid-transition", () => {
  const committed = run(detector(), CROSS_FADE);

  expect(committed.length).toBe(2);
  // The captured content is the settled slide, not a half-drawn frame of the transition.
  expect(hamming(committed[1].hash, hashOf(SLIDE_C))).toBeLessThanOrEqual(4);
});

// Why stableSamples defaults to 3 rather than 2, pinned so it cannot be quietly lowered again.
//
// A cross-fade does not drift smoothly through the hash space. dHash records the *sign* of each
// horizontal comparison, and those signs flip together around the midpoint - so 25% and 50% through a
// fade produce the SAME digest. Two consecutive samples of one intermediate is all a streak of 2 needs,
// and a half-drawn frame commits as though the deck had settled on it.
it("at stableSamples 2 a multi-second transition commits a half-drawn frame", () => {
  const committed = run(detector({ stableSamples: 2 }), CROSS_FADE);

  expect(committed.length).toBe(2);
  const distanceFromSettled = hamming(committed[1].hash, hashOf(SLIDE_C));
  expect(
    distanceFromSettled,
    "expected a mid-transition capture, but it landed on the settled slide",
  ).toBeGreaterThan(4);
});

it("an embedded video playing produces no slides at all while it runs", () => {
  const moving = Array.from({ length: 20 }, (_, i) => stripes(1 + (i % 5)));

  const committed = run(detector(), [...repeat(SLIDE_A, 4), ...moving]);

  expect(committed.length).toBe(1);
});

it("a deck settling after a video captures the slide it lands on", () => {
  const moving = Array.from({ length: 12 }, (_, i) => stripes(1 + (i % 5)));

  const committed = run(detector(), [...repeat(SLIDE_A, 4), ...moving, ...repeat(SLIDE_B, 6)]);

  expect(committed.length).toBe(2);
});

// ---- Back-navigation ------------------------------------------------------------------------------

it("going back to an earlier slide reports it as a repeat rather than a new capture", () => {
  const committed = run(detector(), [
    ...repeat(SLIDE_A, 5),
    ...repeat(SLIDE_B, 5),
    ...repeat(SLIDE_A, 5),
  ]);

  expect(committed.length).toBe(3);
  expect(committed.map((c) => c.duplicate)).toEqual([false, false, true]);
});

it("a repeat still becomes the current slide, so leaving it again is detected normally", () => {
  const committed = run(detector(), [
    ...repeat(SLIDE_A, 5),
    ...repeat(SLIDE_B, 5),
    ...repeat(SLIDE_A, 5),
    ...repeat(SLIDE_C, 5),
  ]);

  // Without this, the revisited slide would not be adopted as the committed state and every later
  // sample would read as a change - re-committing the same slide once per stability window, forever.
  expect(committed.length).toBe(4);
  expect(committed[3].duplicate).toBe(false);
});

it("isDuplicate answers for a slide already captured, and not for a fresh one", () => {
  const det = detector();
  run(det, repeat(SLIDE_A, 5));

  expect(det.isDuplicate(hashOf(SLIDE_A))).toBe(true);
  expect(det.isDuplicate(hashOf(SLIDE_B))).toBe(false);
});

// ---- The commit-time race -------------------------------------------------------------------------
//
// Live, the screen can change between the sample that decided to commit and the full-resolution grab
// that follows it. Filing the NEXT slide's picture under THIS slide's timestamp is worse than missing
// the slide, because nothing about it looks wrong.

it("a slide that changed during its own full-resolution grab is not captured", () => {
  const det = detector();
  const patterns = repeat(SLIDE_A, 4);

  const committed = run(det, patterns, { confirmWith: () => SLIDE_C });

  expect(committed.length).toBe(0);
});

it("the detector recovers after a failed confirmation and captures the next steady run", () => {
  const det = detector();
  let confirmations = 0;

  const committed: SlideCandidate[] = [];
  for (const [i, pattern] of repeat(SLIDE_A, 10).entries()) {
    const commit = det.observe(hashOf(pattern), i * 1000);
    if (!commit) continue;
    // The first confirmation catches a different screen; every later one is honest.
    const seen = ++confirmations === 1 ? SLIDE_C : pattern;
    const outcome = det.confirm(hashOf(seen));
    if (outcome.accepted) committed.push(commit);
  }

  expect(committed.length).toBe(1);
});

it("rejecting a candidate outright - the grab failed, so there is no hash to check - loses only that candidate", () => {
  const det = detector();
  const committed: (SlideCandidate | null)[] = [];

  repeat(SLIDE_A, 10).forEach((pattern, i) => {
    const commit = det.observe(hashOf(pattern), i * 1000);
    if (!commit) return;
    if (committed.length === 0 && i < 5) {
      det.reject();
      committed.push(null); // record the attempt so the next commit is distinguishable
      return;
    }
    if (det.confirm(hashOf(pattern)).accepted) committed.push(commit);
  });

  expect(committed.map((c) => c === null)).toEqual([true, false]);
});

// ---- Configuration --------------------------------------------------------------------------------

it("a slide must hold still for stableSamples before it counts", () => {
  // Two samples of SLIDE_B is not enough at stableSamples = 3, so the deck moving on captures nothing.
  const committed = run(detector({ stableSamples: 3 }), [
    ...repeat(SLIDE_A, 5),
    SLIDE_B,
    SLIDE_B,
    ...repeat(SLIDE_C, 5),
  ]);

  expect(committed.length).toBe(2);
  expect(hamming(committed[1].hash, hashOf(SLIDE_C))).toBeLessThanOrEqual(4);
});

it("a lower stableSamples catches a briskly-paced deck the default would miss", () => {
  const patterns = [...repeat(SLIDE_A, 3), SLIDE_B, SLIDE_B, ...repeat(SLIDE_C, 3)];

  expect(run(detector({ stableSamples: 2 }), patterns).length).toBe(3);
  expect(run(detector({ stableSamples: 3 }), patterns).length).toBe(2);
});
