import { describe, it, expect } from "vitest";
import { buildRecordingTree } from "./recordingTree";
import { childrenOf, breadcrumbOf, recordingCountOf, sectionCreateTarget, depthOf, MAX_FOLDER_DEPTH } from "./drillView";
import type { RecordingSummary, SectionDto } from "./types";

const section = (id: string, name: string, parentId: string | null = null, position = 0): SectionDto =>
  ({ id, name, parentId, position }) as SectionDto;

const recording = (id: string, sectionId: string | null): RecordingSummary =>
  ({ id, title: `rec-${id}`, name: null, sectionId, sectionName: null }) as unknown as RecordingSummary;

// Customers > Ambu, plus a loose recording at the root.
const sections = [
  section("customers", "Customers"),
  section("ambu", "Ambu", "customers"),
  section("podcasts", "Podcasts", null, 1),
];

// A chain exactly MAX_FOLDER_DEPTH deep, for the cap cases.
const deepChain: SectionDto[] = Array.from({ length: MAX_FOLDER_DEPTH }, (_, i) =>
  section(`d${i}`, `L${i}`, i === 0 ? null : `d${i - 1}`),
);
const recordings = [
  recording("r-root", null),
  recording("r-cust", "customers"),
  recording("r-ambu", "ambu"),
  recording("r-ambu2", "ambu"),
];
const tree = buildRecordingTree(recordings, sections);

describe("childrenOf", () => {
  it("at the root: top-level sections plus the ungrouped recordings as direct items", () => {
    const level = childrenOf(tree, null);
    expect(level.sections.map((s) => s.id)).toEqual(["customers", "podcasts"]);
    expect(level.items.map((r) => r.id)).toEqual(["r-root"]);
  });

  it("inside a section: its subsections plus only its own direct recordings", () => {
    const level = childrenOf(tree, "customers");
    expect(level.sections.map((s) => s.id)).toEqual(["ambu"]);
    expect(level.items.map((r) => r.id)).toEqual(["r-cust"]);
  });

  it("inside a leaf subsection: no subsections, its recordings", () => {
    const level = childrenOf(tree, "ambu");
    expect(level.sections).toEqual([]);
    expect(level.items.map((r) => r.id)).toEqual(["r-ambu", "r-ambu2"]);
  });

  // Drilling into a section that has since been deleted must land somewhere, not crash.
  it("returns an empty level for an unknown id", () => {
    expect(childrenOf(tree, "gone")).toEqual({ sections: [], items: [] });
  });
});

describe("breadcrumbOf", () => {
  it("is empty at the root", () => {
    expect(breadcrumbOf(sections, null)).toEqual([]);
  });

  it("walks parentId root-first, ending at the node itself", () => {
    expect(breadcrumbOf(sections, "ambu").map((s) => s.name)).toEqual(["Customers", "Ambu"]);
  });

  it("is just the node for a top-level section", () => {
    expect(breadcrumbOf(sections, "customers").map((s) => s.name)).toEqual(["Customers"]);
  });

  // Written generically against parentId, so lifting the domain's two-level cap needs no nav change.
  it("handles depth beyond two levels", () => {
    const deep = [...sections, section("eu", "EU", "ambu"), section("nordic", "Nordic", "eu")];
    expect(breadcrumbOf(deep, "nordic").map((s) => s.name)).toEqual(["Customers", "Ambu", "EU", "Nordic"]);
  });

  it("returns empty for an unknown id", () => {
    expect(breadcrumbOf(sections, "gone")).toEqual([]);
  });

  // A cycle would hang the panel; parentId is not DB-enforced against one.
  it("terminates on a parent cycle", () => {
    const cyclic = [section("x", "X", "y"), section("y", "Y", "x")];
    expect(() => breadcrumbOf(cyclic, "x")).not.toThrow();
  });
});

describe("depthOf", () => {
  it("counts the root as 0 and a top-level folder as 1", () => {
    expect(depthOf(sections, null)).toBe(0);
    expect(depthOf(sections, "customers")).toBe(1);
    expect(depthOf(sections, "ambu")).toBe(2);
  });

  it("is 0 for an unknown id", () => {
    expect(depthOf(sections, "gone")).toBe(0);
  });
});

describe("sectionCreateTarget", () => {
  it("at the root: a new top-level section", () => {
    expect(sectionCreateTarget(sections, null)).toEqual({ kind: "root" });
  });

  it("inside a top-level section: a sub-section of it", () => {
    expect(sectionCreateTarget(sections, "customers")).toEqual({
      kind: "child",
      parent: sections[0],
    });
  });

  // The cap is now 8 levels, not 1, so a sub-section is an ordinary parent.
  it("inside a sub-section: a sub-section of that", () => {
    expect(sectionCreateTarget(sections, "ambu")).toEqual({
      kind: "child",
      parent: sections[1],
    });
  });

  it("at the maximum depth: blocked", () => {
    const deepest = deepChain[MAX_FOLDER_DEPTH - 1];
    expect(sectionCreateTarget(deepChain, deepest.id)).toEqual({ kind: "blocked" });
  });

  // Drilled into a folder deleted from another tab: creating under a ghost parent would only 404.
  it("blocked for an unknown id rather than falling back to the root", () => {
    expect(sectionCreateTarget(sections, "gone")).toEqual({ kind: "blocked" });
  });
});

describe("recordingCountOf", () => {
  it("counts a section's own recordings plus every descendant's", () => {
    expect(recordingCountOf(tree, "customers")).toBe(3);
  });

  it("counts only its own for a leaf", () => {
    expect(recordingCountOf(tree, "ambu")).toBe(2);
  });

  it("is zero for an empty section", () => {
    expect(recordingCountOf(tree, "podcasts")).toBe(0);
  });
});
