import { describe, it, expect } from "vitest";
import { anchorIndex, weaveTranscript } from "./transcriptNotes";

const seg = (id: string, startMs: number) => ({ id, startMs });
const note = (id: string, capturedAtMs: number | null) => ({ id, capturedAtMs });

describe("anchorIndex", () => {
  it("picks the last segment started at or before the note", () => {
    expect(anchorIndex([0, 1000, 2000], 500)).toBe(0);
    expect(anchorIndex([0, 1000, 2000], 1000)).toBe(1);
    expect(anchorIndex([0, 1000, 2000], 9999)).toBe(2);
  });
  it("is -1 before the first segment", () => {
    expect(anchorIndex([1000, 2000], 500)).toBe(-1);
  });
});

describe("weaveTranscript", () => {
  it("inserts each note after the segment being spoken when it was written", () => {
    const segments = [seg("s0", 0), seg("s1", 1000), seg("s2", 2000)];
    const notes = [note("n1", 1500)]; // during s1
    const rows = weaveTranscript(segments, notes);
    expect(rows.map((r) => (r.kind === "segment" ? r.seg.id : `note:${r.note.id}`))).toEqual([
      "s0", "s1", "note:n1", "s2",
    ]);
  });

  it("keeps a segment's original index for playback highlighting", () => {
    const rows = weaveTranscript([seg("s0", 0), seg("s1", 1000)], [note("n1", 100)]);
    const s1 = rows.find((r) => r.kind === "segment" && r.seg.id === "s1");
    expect(s1?.kind === "segment" && s1.index).toBe(1); // not shifted by the woven note
  });

  it("places a pre-first-segment note at the top and orders co-anchored notes by time", () => {
    const segments = [seg("s0", 1000)];
    const notes = [note("early", 0), note("b", 2000), note("a", 1500)];
    const rows = weaveTranscript(segments, notes);
    expect(rows.map((r) => (r.kind === "segment" ? r.seg.id : r.note.id))).toEqual(["early", "s0", "a", "b"]);
  });

  it("ignores notes with no timestamp (pre-meeting notes)", () => {
    const rows = weaveTranscript([seg("s0", 0)], [note("untimed", null)]);
    expect(rows.every((r) => r.kind === "segment")).toBe(true);
  });
});

describe("weaveTranscript with screenshots", () => {
  const segments = [
    { startMs: 0, id: "s0" },
    { startMs: 10_000, id: "s1" },
  ];

  it("anchors a screenshot after the segment that was being spoken", () => {
    const rows = weaveTranscript(segments, [], [{ capturedAtMs: 4_000, id: "p0" }]);

    expect(rows.map((r) => r.kind)).toEqual(["segment", "screenshot", "segment"]);
  });

  it("puts a screenshot taken before the first segment at the very top", () => {
    const rows = weaveTranscript(segments, [], [{ capturedAtMs: 0, id: "p0" }]);

    expect(rows[0].kind).toBe("segment");
    expect(rows[1].kind).toBe("screenshot");
  });

  it("orders notes and screenshots sharing an anchor by capture time", () => {
    const rows = weaveTranscript(
      segments,
      [{ capturedAtMs: 5_000, id: "n0" }],
      [{ capturedAtMs: 3_000, id: "p0" }],
    );

    expect(rows.map((r) => r.kind)).toEqual(["segment", "screenshot", "note", "segment"]);
  });

  it("ignores screenshots when none are passed", () => {
    const rows = weaveTranscript(segments, [{ capturedAtMs: 1_000, id: "n0" }]);

    expect(rows.map((r) => r.kind)).toEqual(["segment", "note", "segment"]);
  });
});
