import { describe, it, expect } from "vitest";
import { initialSection } from "./detailSection";

describe("initialSection", () => {
  it("opens the hub when nothing is persisted", () => {
    expect(initialSection(null, false)).toBe("hub");
  });

  it("restores a persisted section", () => {
    expect(initialSection("speakers", false)).toBe("speakers");
    expect(initialSection("formulas", false)).toBe("formulas");
  });

  it("migrates the legacy 'overview' tab to the hub (it no longer exists as a section)", () => {
    expect(initialSection("overview", false)).toBe("hub");
  });

  it("migrates the legacy 'attachments' tab to 'files' (the tile's new name)", () => {
    expect(initialSection("attachments", false)).toBe("files");
  });

  it("falls back to the hub for an unrecognised persisted value", () => {
    expect(initialSection("something-else", false)).toBe("hub");
  });

  it("forces the transcript when the URL carries a segment deep-link, whatever was persisted", () => {
    expect(initialSection("speakers", true)).toBe("transcript");
    expect(initialSection(null, true)).toBe("transcript");
    expect(initialSection("overview", true)).toBe("transcript");
  });
});
