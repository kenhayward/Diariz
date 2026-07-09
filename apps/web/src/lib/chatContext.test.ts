import { describe, it, expect } from "vitest";
import { inferCurrentContext, currentContextLabelKey, currentContextRequest } from "./chatContext";

describe("inferCurrentContext", () => {
  it("prefers a 2+ multi-selection (Selected Transcripts)", () => {
    expect(inferCurrentContext({ sectionId: "sec", recordingId: "rec", selectedIds: ["a", "b"] })).toEqual({
      kind: "selected",
      recordingIds: ["a", "b"],
    });
  });

  it("uses the open folder when not multi-selecting", () => {
    expect(inferCurrentContext({ sectionId: "sec", recordingId: null, selectedIds: [] })).toEqual({
      kind: "folder",
      sectionId: "sec",
    });
  });

  it("uses the open recording when no folder and no multi-selection", () => {
    expect(inferCurrentContext({ sectionId: null, recordingId: "rec", selectedIds: ["only-one"] })).toEqual({
      kind: "single",
      recordingId: "rec",
    });
  });

  it("is empty when nothing is open or selected", () => {
    expect(inferCurrentContext({ sectionId: null, recordingId: null, selectedIds: [] })).toEqual({ kind: "empty" });
  });
});

describe("currentContextLabelKey", () => {
  it("maps each kind to its label key", () => {
    expect(currentContextLabelKey({ kind: "folder", sectionId: "s" })).toBe("ctxFolder");
    expect(currentContextLabelKey({ kind: "selected", recordingIds: [] })).toBe("ctxSelected");
    expect(currentContextLabelKey({ kind: "single", recordingId: "r" })).toBe("ctxCurrent");
    expect(currentContextLabelKey({ kind: "empty" })).toBe("ctxCurrent");
  });
});

describe("currentContextRequest", () => {
  it("sends a section id for a folder and recording ids otherwise", () => {
    expect(currentContextRequest({ kind: "folder", sectionId: "s" })).toEqual({ recordingIds: [], sectionId: "s" });
    expect(currentContextRequest({ kind: "single", recordingId: "r" })).toEqual({ recordingIds: ["r"], sectionId: null });
    expect(currentContextRequest({ kind: "selected", recordingIds: ["a", "b"] })).toEqual({ recordingIds: ["a", "b"], sectionId: null });
    expect(currentContextRequest({ kind: "empty" })).toEqual({ recordingIds: [], sectionId: null });
  });
});
