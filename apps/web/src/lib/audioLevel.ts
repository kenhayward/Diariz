// Pure math for the recorder's input-level meter. Kept free of Web Audio / rAF so it's unit-testable
// (mirrors segmentPlayback.ts). InputLevelMeter.tsx owns the AudioContext/AnalyserNode shell and feeds
// the raw analyser bytes through these functions.

/**
 * Root-mean-square amplitude of a `getByteTimeDomainData` buffer. Those bytes are 0..255 centred at 128
 * (128 = silence), so we map each to -1..1 first. Returns 0 (silent) .. ~1 (full-scale).
 */
export function rms(timeData: Uint8Array): number {
  if (timeData.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < timeData.length; i++) {
    const x = (timeData[i] - 128) / 128;
    sum += x * x;
  }
  return Math.sqrt(sum / timeData.length);
}

// Perceptual gain so quiet speech (low RMS) still produces a visible bar without loud speech pegging
// instantly. sqrt compresses the range; the multiplier lifts typical speech into the meaningful band.
const DISPLAY_GAIN = 1.8;

/** Map an RMS amplitude (0..1) to a 0..1 meter fill, with a perceptual curve and clamping. */
export function normalizeLevel(rmsValue: number): number {
  const v = Math.sqrt(Math.max(0, rmsValue)) * DISPLAY_GAIN;
  return Math.min(1, Math.max(0, v));
}

// Meter fill below this (normalized 0..1) counts as "silence" for the hint.
export const SILENCE_LEVEL = 0.05;
// How long the input must stay near-silent before we nudge the user. Long enough to ignore normal
// meeting pauses (decided with the user).
export const SILENCE_HINT_MS = 15_000;

/**
 * Accumulate near-silent time: add `dtMs` while the (normalized) level is below the threshold, or reset
 * to 0 the moment sound returns. The meter shows the hint once this reaches `SILENCE_HINT_MS`.
 */
export function nextSilenceMs(prevMs: number, level: number, dtMs: number): number {
  return level < SILENCE_LEVEL ? prevMs + dtMs : 0;
}
