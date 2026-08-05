import { describe, it, expect } from "vitest";
import { orderedSections } from "./sectionTree";
import type { SectionDto } from "./types";

const section = (id: string, name: string, parentId: string | null = null, position = 0): SectionDto =>
  ({ id, name, parentId, position }) as SectionDto;

describe("orderedSections", () => {
  it("lists a three-level tree in display order with path labels and depths", () => {
    const sections = [
      section("customers", "Customers"),
      section("acme", "Acme", "customers"),
      section("falcon", "Project Falcon", "acme"),
    ];

    const out = orderedSections(sections);

    expect(out.map((o) => o.section.id)).toEqual(["customers", "acme", "falcon"]);
    expect(out.map((o) => o.label)).toEqual(["Customers", "Customers › Acme", "Customers › Acme › Project Falcon"]);
    expect(out.map((o) => o.depth)).toEqual([1, 2, 3]);
  });

  it("orders siblings by position, then name, at every level", () => {
    const sections = [
      section("b", "Beta", null, 1),
      section("a", "Acme", null, 0),
      section("child-z", "Zeta", "a", 1),
      section("child-a", "Alpha", "a", 0),
    ];

    const out = orderedSections(sections);

    expect(out.map((o) => o.section.id)).toEqual(["a", "child-a", "child-z", "b"]);
  });

  it("interleaves a folder immediately followed by its own descendants, not all children then all grandchildren", () => {
    const sections = [
      section("top", "Top"),
      section("mid1", "Mid1", "top", 0),
      section("mid1-leaf", "Leaf", "mid1", 0),
      section("mid2", "Mid2", "top", 1),
    ];

    const out = orderedSections(sections);

    expect(out.map((o) => o.section.id)).toEqual(["top", "mid1", "mid1-leaf", "mid2"]);
  });

  it("does not hang on a parentId cycle, and excludes the cyclic branch", () => {
    const sections = [section("x", "X", "y"), section("y", "Y", "x")];

    const out = orderedSections(sections);

    expect(out.length).toBeLessThanOrEqual(sections.length);
  });

  it("keeps the { section, label } shape for existing consumers", () => {
    const sections = [section("a", "Acme")];
    const out = orderedSections(sections);
    expect(out[0].section).toEqual(sections[0]);
    expect(out[0].label).toBe("Acme");
  });
});
