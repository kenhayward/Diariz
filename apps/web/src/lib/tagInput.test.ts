import { describe, expect, it } from "vitest";
import { addTag, normalizeTag } from "./tagInput";

describe("normalizeTag", () => {
  it("keeps a single word as typed, preserving case", () => {
    expect(normalizeTag("metadata")).toBe("metadata");
    expect(normalizeTag("Roadmap")).toBe("Roadmap");
    expect(normalizeTag("iOS")).toBe("iOS");
  });

  it("joins a phrase with hyphens", () => {
    expect(normalizeTag("budget planning 2026")).toBe("budget-planning-2026");
    expect(normalizeTag("Data Collection")).toBe("Data-Collection");
  });

  it("collapses runs of whitespace and trims the edges", () => {
    expect(normalizeTag("  many   spaces  ")).toBe("many-spaces");
    expect(normalizeTag("line\nbreak\ttab")).toBe("line-break-tab");
  });

  it("trims leading and trailing hyphens", () => {
    expect(normalizeTag("-leading")).toBe("leading");
    expect(normalizeTag("trailing-")).toBe("trailing");
    expect(normalizeTag("--both--")).toBe("both");
  });

  it("returns null when nothing usable is left", () => {
    expect(normalizeTag("")).toBeNull();
    expect(normalizeTag("   ")).toBeNull();
    expect(normalizeTag("-")).toBeNull();
    expect(normalizeTag("---")).toBeNull();
  });

  it("truncates to the column length", () => {
    expect(normalizeTag("x".repeat(100))).toHaveLength(64);
  });
});

describe("addTag", () => {
  it("appends a new tag and reports what it added", () => {
    expect(addTag(["one"], "two")).toEqual({ tags: ["one", "two"], added: "two" });
  });

  it("normalises before adding", () => {
    expect(addTag([], "budget planning")).toEqual({
      tags: ["budget-planning"],
      added: "budget-planning",
    });
  });

  it("rejects a case-insensitive duplicate without changing the list", () => {
    const list = ["metadata"];
    const result = addTag(list, "METADATA");
    expect(result).toEqual({ tags: ["metadata"], added: null });
    expect(result.tags).toEqual(list);
  });

  it("rejects unusable text", () => {
    expect(addTag(["one"], "  ")).toEqual({ tags: ["one"], added: null });
  });

  it("does not mutate the list it was given", () => {
    const list = ["one"];
    addTag(list, "two");
    expect(list).toEqual(["one"]);
  });
});
