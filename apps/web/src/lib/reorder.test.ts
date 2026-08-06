import { describe, it, expect } from "vitest";
import { computeReorder, inDisplayOrder, draggedRecordingIds } from "./reorder";

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

/// Dragging one of several ticked rows moves the whole selection, so computeReorder has to place a *set*
/// while keeping the relative order the rows are shown in.
describe("computeReorder with several dragged items", () => {
  it("inserts them all before the target, keeping their order", () => {
    expect(computeReorder(["a", "b", "c", "d"], ["a", "c"], "d")).toEqual(["b", "a", "c", "d"]);
  });

  it("appends them all when there is no target", () => {
    expect(computeReorder(["a", "b", "c"], ["a", "b"], null)).toEqual(["c", "a", "b"]);
  });

  it("inserts a set dragged in from another group", () => {
    expect(computeReorder(["a", "b"], ["x", "y"], "b")).toEqual(["a", "x", "y", "b"]);
  });

  /// Dropping the set onto one of its own members appends, rather than trying to insert the set before a
  /// row that is itself moving - the single-item rule, generalised.
  it("appends when dropped onto one of the dragged rows", () => {
    expect(computeReorder(["a", "b", "c"], ["b", "c"], "c")).toEqual(["a", "b", "c"]);
  });
});

describe("inDisplayOrder", () => {
  it("sorts ticked ids into the order the rows are shown in", () => {
    expect(inDisplayOrder(["c", "a"], ["a", "b", "c"])).toEqual(["a", "c"]);
  });

  it("keeps ids that are not on screen, after the ones that are", () => {
    expect(inDisplayOrder(["z", "b"], ["a", "b"])).toEqual(["b", "z"]);
  });
});

/// What a drag should carry. Convention (and every file manager) says dragging a row that is NOT part of
/// the selection moves just that row - it would be alarming for an unrelated drag to sweep up ticked rows
/// somewhere off screen.
describe("draggedRecordingIds", () => {
  it("carries the whole selection when the dragged row is part of it", () => {
    expect(draggedRecordingIds("c", ["c", "a"], ["a", "b", "c"])).toEqual(["a", "c"]);
  });

  it("carries only the dragged row when it is not selected", () => {
    expect(draggedRecordingIds("b", ["a", "c"], ["a", "b", "c"])).toEqual(["b"]);
  });

  it("carries only the dragged row when nothing is selected", () => {
    expect(draggedRecordingIds("b", [], ["a", "b", "c"])).toEqual(["b"]);
  });
});
