import { describe, it, expect } from "vitest";
import { buildRecordingTree, reorderBeforeSection, appendSectionUnder } from "./recordingTree";
import type { RecordingSummary, SectionDto } from "./types";

const sec = (id: string, name: string, parentId: string | null = null, position = 0): SectionDto => ({
  id, name, parentId, position,
});
const rec = (id: string, sectionId: string | null, sectionName: string | null = null): RecordingSummary => ({
  id, title: id, name: null, source: "Microphone", durationMs: 0, status: "Transcribed",
  createdAt: "2026-06-29T00:00:00Z", sectionId, sectionName, hasActions: false, hasAudio: true,
});

describe("buildRecordingTree", () => {
  it("nests sub-sections under their parent and files recordings at either level", () => {
    const sections = [
      sec("cust", "Customers", null, 0),
      sec("acme", "Acme", "cust", 0),
      sec("beta", "Beta", "cust", 1),
    ];
    const recordings = [
      rec("r-top", "cust"),   // directly under the top-level section
      rec("r-acme", "acme"),  // under a sub-section
      rec("r-loose", null),   // ungrouped
    ];
    const tree = buildRecordingTree(recordings, sections);

    expect(tree.sections.map((s) => s.name)).toEqual(["Customers"]);
    const customers = tree.sections[0];
    expect(customers.items.map((r) => r.id)).toEqual(["r-top"]);
    expect(customers.children.map((c) => c.name)).toEqual(["Acme", "Beta"]);
    expect(customers.children[0].items.map((r) => r.id)).toEqual(["r-acme"]);
    expect(tree.ungrouped.map((r) => r.id)).toEqual(["r-loose"]);
  });

  it("orders top sections and sub-sections by position then name", () => {
    const sections = [
      sec("b", "B-top", null, 1),
      sec("a", "A-top", null, 0),
      sec("a2", "A-second", "a", 1),
      sec("a1", "A-first", "a", 0),
    ];
    const tree = buildRecordingTree([], sections);
    expect(tree.sections.map((s) => s.name)).toEqual(["A-top", "B-top"]);
    expect(tree.sections[0].children.map((c) => c.name)).toEqual(["A-first", "A-second"]);
  });

  it("falls back to a synthetic top-level group for an unknown section id", () => {
    const tree = buildRecordingTree([rec("r", "missing", "Pending")], []);
    expect(tree.sections).toHaveLength(1);
    expect(tree.sections[0].name).toBe("Pending");
    expect(tree.sections[0].items.map((r) => r.id)).toEqual(["r"]);
  });
});

describe("reorderBeforeSection", () => {
  const sections = [
    sec("cust", "Customers", null, 0),
    sec("vend", "Vendors", null, 1),
    sec("acme", "Acme", "cust", 0),
    sec("beta", "Beta", "cust", 1),
  ];

  it("reorders a sub-section before a sibling under the same parent", () => {
    expect(reorderBeforeSection(sections, "beta", "acme")).toEqual({
      parentId: "cust",
      orderedIds: ["beta", "acme"],
    });
  });

  it("reparents by dropping before a section in a different parent (here: a top-level section)", () => {
    // Drop sub-section "acme" before top-level "vend" → acme becomes top-level, before Vendors.
    expect(reorderBeforeSection(sections, "acme", "vend")).toEqual({
      parentId: null,
      orderedIds: ["cust", "acme", "vend"],
    });
  });

  it("is a no-op when dropping a section on itself", () => {
    expect(reorderBeforeSection(sections, "acme", "acme")).toBeNull();
  });
});

describe("appendSectionUnder", () => {
  const sections = [
    sec("cust", "Customers", null, 0),
    sec("acme", "Acme", "cust", 0),
    sec("loose", "Loose", null, 1),
  ];

  it("appends a section as the last child of a parent", () => {
    expect(appendSectionUnder(sections, "loose", "cust")).toEqual({
      parentId: "cust",
      orderedIds: ["acme", "loose"],
    });
  });

  it("promotes a sub-section to the top level", () => {
    expect(appendSectionUnder(sections, "acme", null)).toEqual({
      parentId: null,
      orderedIds: ["cust", "loose", "acme"],
    });
  });
});
