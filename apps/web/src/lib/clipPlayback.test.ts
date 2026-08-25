import { describe, expect, it } from "vitest";
import { clipQueue } from "./clipPlayback";

describe("clipQueue", () => {
  const segments = [
    { id: "b", startMs: 5000, endMs: 6000 },
    { id: "a", startMs: 1000, endMs: 2000 },
    { id: "c", startMs: 6000, endMs: 9500 },
  ];

  it("plays the selection in time order, not in the order it was ticked", () => {
    expect(clipQueue(segments, ["b", "a"]).map((c) => c.segmentId)).toEqual(["a", "b"]);
  });

  it("is empty when nothing is selected", () => {
    expect(clipQueue(segments, [])).toEqual([]);
  });

  it("ignores an id that is not in the segment list", () => {
    // A stale selection survives a re-transcribe, which replaces every segment row.
    expect(clipQueue(segments, ["a", "gone"]).map((c) => c.segmentId)).toEqual(["a"]);
  });

  it("keeps touching segments as separate clips", () => {
    // Deliberately no merging, even though "b" ends exactly where "c" begins. The clip endpoint only serves
    // a span that falls inside a single segment - a merged span would be refused as 404, and relaxing that
    // guard is what keeps the assessment permission from reaching arbitrary audio.
    const q = clipQueue(segments, ["b", "c"]);
    expect(q).toHaveLength(2);
    expect(q[0]).toEqual({ segmentId: "b", fromMs: 5000, toMs: 6000 });
    expect(q[1]).toEqual({ segmentId: "c", fromMs: 6000, toMs: 9500 });
  });

  it("carries the exact span of each segment", () => {
    expect(clipQueue(segments, ["a"])).toEqual([{ segmentId: "a", fromMs: 1000, toMs: 2000 }]);
  });

  it("accepts a Set as well as an array", () => {
    expect(clipQueue(segments, new Set(["c"])).map((c) => c.segmentId)).toEqual(["c"]);
  });
});
