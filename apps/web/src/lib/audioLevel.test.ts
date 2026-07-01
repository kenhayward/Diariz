import { describe, it, expect } from "vitest";
import {
  rms,
  normalizeLevel,
  nextSilenceMs,
  SILENCE_LEVEL,
  SILENCE_HINT_MS,
} from "./audioLevel";

// getByteTimeDomainData yields 0..255 centred at 128 (silence). Build fixtures around that.
const filled = (value: number, n = 256) => Uint8Array.from({ length: n }, () => value);
// A full-scale square wave alternates between the extremes.
const square = (n = 256) => Uint8Array.from({ length: n }, (_, i) => (i % 2 === 0 ? 255 : 0));

describe("rms", () => {
  it("is 0 for a silent (all-128) buffer", () => {
    expect(rms(filled(128))).toBe(0);
  });

  it("is ~1 for a full-scale square wave", () => {
    expect(rms(square())).toBeCloseTo(1, 2);
  });

  it("is between 0 and 1 for a partial signal, and grows with amplitude", () => {
    const quiet = rms(filled(140)); // small offset from centre
    const loud = rms(filled(200));
    expect(quiet).toBeGreaterThan(0);
    expect(quiet).toBeLessThan(loud);
    expect(loud).toBeLessThanOrEqual(1);
  });
});

describe("normalizeLevel", () => {
  it("maps silence to 0 and clamps to [0,1]", () => {
    expect(normalizeLevel(0)).toBe(0);
    expect(normalizeLevel(1)).toBe(1);
    expect(normalizeLevel(5)).toBe(1); // clamped
  });

  it("is monotonic increasing", () => {
    expect(normalizeLevel(0.01)).toBeLessThan(normalizeLevel(0.04));
    expect(normalizeLevel(0.04)).toBeLessThan(normalizeLevel(0.25));
  });

  it("lifts quiet speech to a visible fill", () => {
    expect(normalizeLevel(0.05)).toBeGreaterThan(0.2);
  });
});

describe("nextSilenceMs", () => {
  it("accumulates elapsed time while the level stays below the silence threshold", () => {
    const quiet = SILENCE_LEVEL / 2;
    expect(nextSilenceMs(1000, quiet, 250)).toBe(1250);
  });

  it("resets to 0 as soon as sound is detected", () => {
    expect(nextSilenceMs(10_000, 0.5, 250)).toBe(0);
  });

  it("crosses the hint threshold after ~15s of continuous silence", () => {
    let acc = 0;
    for (let i = 0; i < 60; i++) acc = nextSilenceMs(acc, 0, 250); // 60 × 250ms = 15s
    expect(acc).toBeGreaterThanOrEqual(SILENCE_HINT_MS);
  });
});
