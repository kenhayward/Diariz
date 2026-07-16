import { describe, it, expect } from "vitest";
import { SECTION_PALETTE, sectionColor } from "./sectionColors";

describe("sectionColor", () => {
  it("is stable for the same id", () => {
    const id = "8f14e45f-ceea-467a-9c1a-2b4e6d1f0a33";
    expect(sectionColor(id)).toEqual(sectionColor(id));
  });

  // Pins the hash so a refactor can't silently re-colour every folder in every user's panel.
  it("maps known ids to known palette entries", () => {
    expect(sectionColor("a")).toBe(SECTION_PALETTE[0]);
    expect(sectionColor("b")).toBe(SECTION_PALETTE[2]);
    expect(sectionColor("customers")).toBe(SECTION_PALETTE[4]);
  });

  it("distributes across the whole palette", () => {
    const ids = Array.from({ length: 200 }, (_, i) => `section-${i}`);
    const used = new Set(ids.map((id) => sectionColor(id)));
    expect(used.size).toBe(SECTION_PALETTE.length);
  });

  it("gives every entry a light and a dark hex", () => {
    for (const entry of SECTION_PALETTE) {
      expect(entry.light).toMatch(/^#[0-9a-f]{6}$/);
      expect(entry.dark).toMatch(/^#[0-9a-f]{6}$/);
    }
  });

  // A section rendered before its id lands (or a synthetic node) must not crash the panel.
  it("does not throw on an empty id", () => {
    expect(() => sectionColor("")).not.toThrow();
    expect(SECTION_PALETTE).toContain(sectionColor(""));
  });
});
