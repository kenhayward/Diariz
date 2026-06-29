import { describe, it, expect } from "vitest";
import { hasRevisions, segmentText, toggleLabel } from "./transcriptView";
import type { SegmentDto } from "./types";

function seg(partial: Partial<SegmentDto>): SegmentDto {
  return {
    id: "s",
    speaker: "SPEAKER_00",
    speakerDisplay: "Alice",
    startMs: 0,
    endMs: 1000,
    original: "the original",
    revised: null,
    text: "the original",
    ...partial,
  };
}

describe("hasRevisions", () => {
  it("is false when no segment is revised", () => {
    expect(hasRevisions([seg({}), seg({ revised: null })])).toBe(false);
  });

  it("is true when any segment has a revision (including a blank one)", () => {
    expect(hasRevisions([seg({}), seg({ revised: "edited", text: "edited" })])).toBe(true);
    expect(hasRevisions([seg({ revised: "", text: "" })])).toBe(true);
  });

  it("is false for an empty list", () => {
    expect(hasRevisions([])).toBe(false);
  });
});

describe("segmentText", () => {
  it("shows effective text by default (revision when present)", () => {
    expect(segmentText(seg({ revised: "edited", text: "edited" }), false)).toBe("edited");
    expect(segmentText(seg({}), false)).toBe("the original");
  });

  it("forces the model original when showOriginal is true", () => {
    expect(segmentText(seg({ revised: "edited", text: "edited" }), true)).toBe("the original");
  });
});

describe("toggleLabel", () => {
  it("offers to show the original when currently showing the revised", () => {
    expect(toggleLabel(false)).toBe("Show original");
  });

  it("offers to show the revised when currently showing the original", () => {
    expect(toggleLabel(true)).toBe("Show revised");
  });
});
