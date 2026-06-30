import { describe, it, expect } from "vitest";
import { speakerRanges, rangeAt, nextRangeStart, selectedRanges } from "./segmentPlayback";

type Seg = { speaker: string; startMs: number; endMs: number };

const segs: Seg[] = [
  { speaker: "SPEAKER_00", startMs: 0, endMs: 1000 },
  { speaker: "SPEAKER_01", startMs: 1000, endMs: 2000 },
  { speaker: "SPEAKER_00", startMs: 2000, endMs: 2500 }, // contiguous with the 4th once 01 is removed? no — gap is 01
  { speaker: "SPEAKER_00", startMs: 2500, endMs: 3000 }, // touches the previous SPEAKER_00 range
  { speaker: "SPEAKER_01", startMs: 3000, endMs: 4000 },
];

describe("speakerRanges", () => {
  it("filters to one speaker, sorts, and merges touching/contiguous ranges", () => {
    expect(speakerRanges(segs, "SPEAKER_00")).toEqual([
      { start: 0, end: 1000 },
      { start: 2000, end: 3000 }, // 2000-2500 and 2500-3000 merged (touching)
    ]);
  });

  it("returns ranges for the other speaker untouched", () => {
    expect(speakerRanges(segs, "SPEAKER_01")).toEqual([
      { start: 1000, end: 2000 },
      { start: 3000, end: 4000 },
    ]);
  });

  it("sorts out-of-order input", () => {
    const out: Seg[] = [
      { speaker: "A", startMs: 5000, endMs: 6000 },
      { speaker: "A", startMs: 1000, endMs: 2000 },
    ];
    expect(speakerRanges(out, "A")).toEqual([
      { start: 1000, end: 2000 },
      { start: 5000, end: 6000 },
    ]);
  });

  it("returns [] when the speaker has no segments", () => {
    expect(speakerRanges(segs, "SPEAKER_99")).toEqual([]);
  });
});

describe("selectedRanges", () => {
  const idSegs = [
    { id: "a", startMs: 0, endMs: 1000 },
    { id: "b", startMs: 1000, endMs: 2000 },
    { id: "c", startMs: 2000, endMs: 2500 },
    { id: "d", startMs: 2500, endMs: 3000 },
  ];

  it("keeps only the selected ids, sorted, and merges touching ranges", () => {
    expect(selectedRanges(idSegs, ["a", "c", "d"])).toEqual([
      { start: 0, end: 1000 },
      { start: 2000, end: 3000 }, // c + d touch → merged
    ]);
  });

  it("leaves a gap between non-adjacent selections", () => {
    expect(selectedRanges(idSegs, ["a", "d"])).toEqual([
      { start: 0, end: 1000 },
      { start: 2500, end: 3000 },
    ]);
  });

  it("accepts a Set and returns [] for an empty selection", () => {
    expect(selectedRanges(idSegs, new Set(["b"]))).toEqual([{ start: 1000, end: 2000 }]);
    expect(selectedRanges(idSegs, [])).toEqual([]);
  });
});

describe("rangeAt", () => {
  const ranges = [
    { start: 0, end: 1000 },
    { start: 2000, end: 3000 },
  ];

  it("returns the range containing ms (start inclusive, end exclusive)", () => {
    expect(rangeAt(ranges, 0)).toEqual({ start: 0, end: 1000 });
    expect(rangeAt(ranges, 999)).toEqual({ start: 0, end: 1000 });
    expect(rangeAt(ranges, 2500)).toEqual({ start: 2000, end: 3000 });
  });

  it("returns null in a gap, at a range end, or past the end", () => {
    expect(rangeAt(ranges, 1000)).toBeNull(); // end is exclusive
    expect(rangeAt(ranges, 1500)).toBeNull(); // gap
    expect(rangeAt(ranges, 9999)).toBeNull();
  });
});

describe("nextRangeStart", () => {
  const ranges = [
    { start: 0, end: 1000 },
    { start: 2000, end: 3000 },
  ];

  it("returns the start of the first range beginning after ms", () => {
    expect(nextRangeStart(ranges, -1)).toBe(0); // before everything → first range
    expect(nextRangeStart(ranges, 1000)).toBe(2000); // at the gap → jump to next range
    expect(nextRangeStart(ranges, 1999)).toBe(2000);
  });

  it("returns null once no range starts after ms (playback should stop)", () => {
    expect(nextRangeStart(ranges, 2000)).toBeNull();
    expect(nextRangeStart(ranges, 3000)).toBeNull();
  });

  it("returns null for an empty range list", () => {
    expect(nextRangeStart([], 0)).toBeNull();
  });
});
