import { describe, it, expect } from "vitest";
import { flowSpans, speakerShares, seekMsFromFraction } from "./conversationFlow";

const seg = (speaker: string, startMs: number, endMs: number) => ({ speaker, startMs, endMs });

describe("flowSpans", () => {
  it("turns each segment into a span whose width is its share of the duration", () => {
    const spans = flowSpans([seg("A", 0, 5000), seg("B", 5000, 10000)], 10000);
    expect(spans.map((s) => s.label)).toEqual(["A", "B"]);
    expect(spans.map((s) => s.widthPct)).toEqual([50, 50]);
  });

  it("emits a silence span (label null) for a gap between segments", () => {
    const spans = flowSpans([seg("A", 0, 2000), seg("B", 8000, 10000)], 10000);
    expect(spans.map((s) => s.label)).toEqual(["A", null, "B"]);
    expect(spans[1].startMs).toBe(2000);
    expect(spans[1].endMs).toBe(8000);
    expect(spans[1].widthPct).toBe(60);
  });

  it("emits leading and trailing silence", () => {
    const spans = flowSpans([seg("A", 2000, 8000)], 10000);
    expect(spans.map((s) => s.label)).toEqual([null, "A", null]);
  });

  it("merges consecutive segments from the same speaker into one span", () => {
    const spans = flowSpans([seg("A", 0, 4000), seg("A", 4000, 10000)], 10000);
    expect(spans.map((s) => s.label)).toEqual(["A"]);
    expect(spans[0].widthPct).toBe(100);
  });

  it("produces widths that sum to 100", () => {
    const spans = flowSpans([seg("A", 0, 1000), seg("B", 3000, 4000), seg("A", 7000, 9000)], 10000);
    const total = spans.reduce((sum, s) => sum + s.widthPct, 0);
    expect(Math.round(total)).toBe(100);
  });

  it("sorts out-of-order segments before laying them out", () => {
    const spans = flowSpans([seg("B", 5000, 10000), seg("A", 0, 5000)], 10000);
    expect(spans.map((s) => s.label)).toEqual(["A", "B"]);
  });

  it("is empty when there are no segments", () => {
    expect(flowSpans([], 10000)).toEqual([]);
  });

  it("is empty when the duration is zero (nothing to lay out against)", () => {
    expect(flowSpans([seg("A", 0, 1000)], 0)).toEqual([]);
  });
});

describe("speakerShares", () => {
  it("gives each speaker their percentage of total talk time, largest first", () => {
    const shares = speakerShares([seg("A", 0, 3000), seg("B", 3000, 4000)]);
    expect(shares).toEqual([
      { label: "A", pct: 75 },
      { label: "B", pct: 25 },
    ]);
  });

  it("sums a speaker's non-contiguous segments", () => {
    // A speaks 1s + 1s, B speaks 2s: an even split, so this also pins the tie-break (by label).
    const shares = speakerShares([seg("B", 1000, 3000), seg("A", 0, 1000), seg("A", 3000, 4000)]);
    expect(shares).toEqual([
      { label: "A", pct: 50 },
      { label: "B", pct: 50 },
    ]);
  });

  it("ignores silence — shares are of talk time, not of the recording", () => {
    // 2s of talk in a 10s recording: the two speakers still split 100% between them.
    const shares = speakerShares([seg("A", 0, 1000), seg("B", 9000, 10000)]);
    expect(shares.reduce((sum, s) => sum + s.pct, 0)).toBe(100);
  });

  it("is empty for no segments", () => {
    expect(speakerShares([])).toEqual([]);
  });
});

describe("seekMsFromFraction", () => {
  it("maps a fraction of the track to a position in the recording", () => {
    expect(seekMsFromFraction(0.25, 20000)).toBe(5000);
  });

  it("clamps below zero and above one (a drag can run past either end of the track)", () => {
    expect(seekMsFromFraction(-0.5, 20000)).toBe(0);
    expect(seekMsFromFraction(1.5, 20000)).toBe(20000);
  });

  it("is zero for a zero-duration recording", () => {
    expect(seekMsFromFraction(0.5, 0)).toBe(0);
  });
});
