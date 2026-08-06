import { describe, it, expect } from "vitest";
import type { DragEvent } from "react";
import { dragHasFiles, SECTION_MIME } from "./dragTypes";

/// A drag event carries only what the handler reads: the payload types it advertises.
const drag = (types: string[] | undefined) => ({ dataTransfer: { types } }) as unknown as DragEvent;

describe("dragHasFiles", () => {
  it("is true for an OS file drag", () => {
    expect(dragHasFiles(drag(["Files"]))).toBe(true);
  });

  it("is false for the panel's own reorder drags", () => {
    // These share the same drop handlers, so mistaking one for a file drag would swallow a reorder.
    expect(dragHasFiles(drag(["text/plain"]))).toBe(false);
    expect(dragHasFiles(drag([SECTION_MIME]))).toBe(false);
  });

  it("is true when a file drag also advertises other types", () => {
    // Dragging from a file manager commonly offers text/uri-list alongside Files.
    expect(dragHasFiles(drag(["text/uri-list", "Files"]))).toBe(true);
  });

  it("is false when the browser reports no types at all", () => {
    expect(dragHasFiles(drag([]))).toBe(false);
    expect(dragHasFiles(drag(undefined))).toBe(false);
  });
});
