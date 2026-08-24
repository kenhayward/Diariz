import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import AttachmentDragHandle from "./AttachmentDragHandle";
import { ATTACHMENT_DRAG_TYPE } from "../lib/dragTypes";

describe("AttachmentDragHandle", () => {
  function dragIt(scope: "recording" | "section" = "recording") {
    const setData = vi.fn();
    render(<AttachmentDragHandle scope={scope} ownerId="rec-1" attachmentId="att-1" name="Plan.pdf" />);
    fireEvent.dragStart(screen.getByRole("button"), { dataTransfer: { setData, effectAllowed: "" } });
    return setData;
  }

  it("puts the attachment payload on the drag", () => {
    const setData = dragIt();

    expect(setData).toHaveBeenCalledWith(
      ATTACHMENT_DRAG_TYPE,
      JSON.stringify({ scope: "recording", ownerId: "rec-1", attachmentId: "att-1", name: "Plan.pdf" }),
    );
  });

  /// The recordings panel reads a bare text/plain drag as a recording id being reordered, so setting one here
  /// would let an attachment drag land as a reorder. Only our own type goes on the drag.
  it("sets no other data type", () => {
    expect(dragIt()).toHaveBeenCalledTimes(1);
  });

  it("carries the section scope through unchanged", () => {
    const setData = dragIt("section");

    expect(setData.mock.calls[0][1]).toContain('"scope":"section"');
  });

  it("is marked draggable", () => {
    render(<AttachmentDragHandle scope="section" ownerId="sec-1" attachmentId="att-2" name="Brief.docx" />);

    expect(screen.getByRole("button").getAttribute("draggable")).toBe("true");
  });
});
