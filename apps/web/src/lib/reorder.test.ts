import { describe, it, expect } from "vitest";
import { computeReorder } from "./reorder";

describe("computeReorder", () => {
  it("moves an item up by inserting before a target", () => {
    expect(computeReorder(["a", "b", "c"], "c", "a")).toEqual(["c", "a", "b"]);
  });

  it("appends when there is no target (drop on empty group space)", () => {
    expect(computeReorder(["a", "b", "c"], "a", null)).toEqual(["b", "c", "a"]);
  });

  it("inserts an item dragged in from another group", () => {
    expect(computeReorder(["a", "b"], "x", "b")).toEqual(["a", "x", "b"]);
  });

  it("appends a cross-group item when dropped on the group", () => {
    expect(computeReorder(["a", "b"], "x", null)).toEqual(["a", "b", "x"]);
  });

  it("is a no-op-ish move when dropped on itself (goes to end)", () => {
    expect(computeReorder(["a", "b", "c"], "b", "b")).toEqual(["a", "c", "b"]);
  });
});
