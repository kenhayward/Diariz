import { describe, it, expect } from "vitest";
import {
  addSection, removeSection, updateSection, moveSection,
  addBlock, removeBlock, updateBlock, moveBlock, moveBlockCrossSection, normalizeBreaks, contentError,
} from "./meetingTypeDraft";
import type { MeetingTypeContent } from "./types";

const base: MeetingTypeContent = {
  sections: [
    { level: 1, title: "A", blocks: [{ kind: "boilerplate", text: "x" }] },
    { level: 1, title: "B", blocks: [] },
  ],
};

describe("section operations (immutable)", () => {
  it("adds a section without mutating the input", () => {
    const out = addSection(base, 2);
    expect(out.sections).toHaveLength(3);
    expect(out.sections[2]).toEqual({ level: 2, title: "", blocks: [] });
    expect(base.sections).toHaveLength(2); // unchanged
  });

  it("removes a section by index", () => {
    expect(removeSection(base, 0).sections.map((s) => s.title)).toEqual(["B"]);
  });

  it("patches a section's fields", () => {
    expect(updateSection(base, 0, { title: "Renamed", level: 2 }).sections[0]).toMatchObject({
      title: "Renamed",
      level: 2,
    });
  });

  it("reorders sections", () => {
    expect(moveSection(base, 0, 1).sections.map((s) => s.title)).toEqual(["B", "A"]);
  });
});

describe("block operations (within a section)", () => {
  it("adds a field block with a default field", () => {
    const out = addBlock(base, 1, "field");
    expect(out.sections[1].blocks).toEqual([{ kind: "field", field: "date", breakAfter: "none" }]);
  });

  it("adds a prompt block with empty text", () => {
    expect(addBlock(base, 1, "prompt").sections[1].blocks[0]).toEqual({
      kind: "prompt", text: "", breakAfter: "paragraph",
    });
  });

  it("updates a block", () => {
    const out = updateBlock(base, 0, 0, { text: "changed" });
    expect(out.sections[0].blocks[0]).toEqual({ kind: "boilerplate", text: "changed" });
  });

  it("removes and reorders blocks", () => {
    const c: MeetingTypeContent = {
      sections: [{ level: 1, title: "S", blocks: [{ kind: "boilerplate", text: "1" }, { kind: "prompt", text: "2" }] }],
    };
    expect(moveBlock(c, 0, 0, 1).sections[0].blocks.map((b) => b.text)).toEqual(["2", "1"]);
    expect(removeBlock(c, 0, 0).sections[0].blocks.map((b) => b.text)).toEqual(["2"]);
  });
});

describe("moveBlockCrossSection", () => {
  const two: MeetingTypeContent = {
    sections: [
      { level: 1, title: "A", blocks: [{ kind: "boilerplate", text: "a0" }, { kind: "boilerplate", text: "a1" }] },
      { level: 1, title: "B", blocks: [{ kind: "boilerplate", text: "b0" }] },
    ],
  };

  it("moves a block into another section at the given index", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 0 }, { section: 1, index: 0 });
    expect(out.sections[0].blocks.map((b) => b.text)).toEqual(["a1"]);
    expect(out.sections[1].blocks.map((b) => b.text)).toEqual(["a0", "b0"]);
    expect(two.sections[0].blocks).toHaveLength(2); // input unchanged
  });

  it("appends when the destination index is past the end", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 1 }, { section: 1, index: 99 });
    expect(out.sections[1].blocks.map((b) => b.text)).toEqual(["b0", "a1"]);
  });

  it("delegates a same-section move to reordering", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 0 }, { section: 0, index: 1 });
    expect(out.sections[0].blocks.map((b) => b.text)).toEqual(["a1", "a0"]);
  });

  it("is a no-op for an out-of-range source", () => {
    expect(moveBlockCrossSection(two, { section: 5, index: 0 }, { section: 1, index: 0 })).toBe(two);
  });
});

describe("contentError", () => {
  it("passes a well-formed template", () => {
    expect(contentError({ sections: [{ level: 1, title: "S", blocks: [{ kind: "field", field: "date" }] }] })).toBeNull();
  });

  it("flags an empty section title", () => {
    expect(contentError({ sections: [{ level: 1, title: " ", blocks: [] }] })).toBe("sectionTitleRequired");
  });

  it("flags empty prompt/boilerplate text", () => {
    expect(contentError({ sections: [{ level: 1, title: "S", blocks: [{ kind: "prompt", text: "" }] }] })).toBe(
      "blockTextRequired",
    );
  });

  it("flags an unknown field", () => {
    expect(contentError({ sections: [{ level: 1, title: "S", blocks: [{ kind: "field", field: "salary" }] }] })).toBe(
      "blockFieldRequired",
    );
  });
});
