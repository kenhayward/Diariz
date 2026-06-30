import { describe, it, expect } from "vitest";
import { parseRecordingLink, recordingLinkPath, segmentIndexAtMs } from "./transcriptNav";

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
