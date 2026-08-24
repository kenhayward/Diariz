import { describe, expect, it } from "vitest";
import { isSelected, spansForSegments, type Span } from "./voiceprintSelection";

const spans = [
  { startMs: 1000, endMs: 2000 },
  { startMs: 5000, endMs: 6500 },
];

describe("isSelected", () => {
  it("treats no spans as the whole speaker", () => {
    // Which is what every sample enrolled before selection existed does, so an unedited voiceprint must
    // show every segment ticked rather than none.
    expect(isSelected({ startMs: 9000, endMs: 9500 }, [])).toBe(true);
  });

  it("selects a segment inside a span", () => {
    expect(isSelected({ startMs: 1200, endMs: 1800 }, spans)).toBe(true);
    expect(isSelected({ startMs: 1000, endMs: 2000 }, spans)).toBe(true);
  });

  it("does not select a segment between the spans", () => {
    expect(isSelected({ startMs: 3000, endMs: 4000 }, spans)).toBe(false);
  });

  it("does not select a segment only partly covered", () => {
    // Only reachable after a re-transcribe moved the boundaries under an older selection. Half a segment
    // is not a segment, and ticking it would claim audio the user never chose.
    expect(isSelected({ startMs: 1500, endMs: 2500 }, spans)).toBe(false);
  });

  it("selects a segment spanning two touching spans", () => {
    expect(isSelected({ startMs: 1500, endMs: 2500 }, [
      { startMs: 1000, endMs: 2000 },
      { startMs: 2000, endMs: 3000 },
    ])).toBe(true);
  });
});

describe("spansForSegments", () => {
  it("collapses adjacent picks", () => {
    // One span per ticked segment would make a long selection hundreds of entries in the job payload.
    expect(spansForSegments([
      { startMs: 2000, endMs: 3000 },
      { startMs: 1000, endMs: 2000 },
      { startMs: 5000, endMs: 6000 },
    ])).toEqual([
      { startMs: 1000, endMs: 3000 },
      { startMs: 5000, endMs: 6000 },
    ]);
  });

  it("returns bare spans, not the objects it was given", () => {
    // Callers pass whole segments. Spreading them would put the transcript text and ids into the request
    // body - which the server ignores, so it would be invisible until someone read the wire.
    const out = spansForSegments([
      { startMs: 0, endMs: 1000, id: "g1", text: "One" } as unknown as Span,
    ]);

    expect(out).toEqual([{ startMs: 0, endMs: 1000 }]);
    expect(Object.keys(out[0]).sort()).toEqual(["endMs", "startMs"]);
  });

  it("is empty for nothing picked", () => {
    expect(spansForSegments([])).toEqual([]);
  });

  it("round-trips through isSelected", () => {
    // The property the UI depends on: tick a set, save it, re-read it, and exactly those are ticked. If
    // this drifted, a selection would appear to change itself the moment it was saved.
    const picked = [
      { startMs: 1000, endMs: 2000 },
      { startMs: 2000, endMs: 3000 },
      { startMs: 7000, endMs: 8000 },
    ];
    const saved = spansForSegments(picked);

    for (const seg of picked) expect(isSelected(seg, saved)).toBe(true);
    expect(isSelected({ startMs: 4000, endMs: 5000 }, saved)).toBe(false);
  });
});
