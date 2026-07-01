import { describe, it, expect } from "vitest";
import {
  parseRecordingLink,
  recordingLinkPath,
  parseMatchTimes,
  segmentIndexAtMs,
  nextSpeakerSegment,
  prevSpeakerSegment,
} from "./transcriptNav";

describe("parseRecordingLink", () => {
  it("parses a recording link with a time", () => {
    expect(parseRecordingLink("/recordings/abc-123?t=252000")).toEqual({ id: "abc-123", t: 252000 });
  });

  it("parses a whole-recording link", () => {
    expect(parseRecordingLink("/recordings/abc-123")).toEqual({ id: "abc-123", t: null });
  });

  it("ignores non-recording and external links", () => {
    expect(parseRecordingLink("/people")).toBeNull();
    expect(parseRecordingLink("https://evil.example.com/recordings/x")).toBeNull();
    expect(parseRecordingLink("mailto:a@b.c")).toBeNull();
  });

  it("ignores a non-numeric time", () => {
    expect(parseRecordingLink("/recordings/x?t=abc")).toEqual({ id: "x", t: null });
  });
});

describe("recordingLinkPath", () => {
  it("round-trips", () => {
    expect(recordingLinkPath({ id: "x", t: 5000 })).toBe("/recordings/x?t=5000");
    expect(recordingLinkPath({ id: "x", t: null })).toBe("/recordings/x");
  });

  it("carries all cited moments as ts for prev/next navigation", () => {
    expect(recordingLinkPath({ id: "x", t: 5000 }, [1000, 5000, 9000])).toBe(
      "/recordings/x?t=5000&ts=1000%2C5000%2C9000",
    );
  });

  it("omits ts when there's only one moment", () => {
    expect(recordingLinkPath({ id: "x", t: 5000 }, [5000])).toBe("/recordings/x?t=5000");
  });
});

describe("parseMatchTimes", () => {
  it("parses, dedups and sorts", () => {
    expect(parseMatchTimes("9000,1000,5000,1000")).toEqual([1000, 5000, 9000]);
  });
  it("handles empty / junk", () => {
    expect(parseMatchTimes(null)).toEqual([]);
    expect(parseMatchTimes("a,b")).toEqual([]);
  });
});

describe("segmentIndexAtMs", () => {
  const segs = [
    { startMs: 0, endMs: 1000 },
    { startMs: 1000, endMs: 2000 },
    { startMs: 5000, endMs: 6000 },
  ];

  it("finds the containing segment", () => {
    expect(segmentIndexAtMs(segs, 1500)).toBe(1);
  });

  it("falls back to the nearest by start", () => {
    expect(segmentIndexAtMs(segs, 4000)).toBe(2); // closest start is 5000
  });

  it("returns -1 for an empty list", () => {
    expect(segmentIndexAtMs([], 100)).toBe(-1);
  });
});

describe("nextSpeakerSegment / prevSpeakerSegment", () => {
  // A: 0,3 · B: 1,2,4
  const segs = [
    { speaker: "A" },
    { speaker: "B" },
    { speaker: "B" },
    { speaker: "A" },
    { speaker: "B" },
  ];

  it("finds the first segment for a speaker when nothing is highlighted", () => {
    expect(nextSpeakerSegment(segs, "A", -1)).toBe(0);
    expect(nextSpeakerSegment(segs, "B", -1)).toBe(1);
    expect(prevSpeakerSegment(segs, "A", segs.length)).toBe(3);
    expect(prevSpeakerSegment(segs, "B", segs.length)).toBe(4);
  });

  it("moves to the correct neighbour from a highlighted position", () => {
    expect(nextSpeakerSegment(segs, "A", 0)).toBe(3);
    expect(nextSpeakerSegment(segs, "B", 1)).toBe(2);
    expect(prevSpeakerSegment(segs, "B", 4)).toBe(2);
    expect(prevSpeakerSegment(segs, "A", 3)).toBe(0);
  });

  it("clamps at the ends (null when there is no further segment)", () => {
    expect(nextSpeakerSegment(segs, "A", 3)).toBeNull();
    expect(prevSpeakerSegment(segs, "A", 0)).toBeNull();
    expect(nextSpeakerSegment(segs, "C", -1)).toBeNull(); // unknown speaker
  });
});
