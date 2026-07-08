import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import TagCloudModal from "./TagCloudModal";
import type { RecordingSummary, TagCloudEntry } from "../lib/types";

const recordings = [
  { id: "a", name: "Budget call", title: "Mic 1", durationMs: 9000, hasAudio: true } as RecordingSummary,
  { id: "b", name: "Vendor call", title: "Mic 2", durationMs: 61000, hasAudio: true } as RecordingSummary,
];

const tags: TagCloudEntry[] = [
  { tag: "Budget Planning", count: 1, weight: 0.9, recordingIds: ["a"] },
  { tag: "Vendor Selection", count: 1, weight: 0.4, recordingIds: ["b"] },
];

function renderModal(over: Partial<Parameters<typeof TagCloudModal>[0]> = {}) {
  const props = {
    tags,
    recordings,
    selected: null as string | null,
    onSelect: vi.fn(),
    onClose: vi.fn(),
    ...over,
  };
  render(
    <MemoryRouter initialEntries={["/"]}>
      <Routes>
        <Route path="/" element={<TagCloudModal {...props} />} />
        <Route path="/recordings/:id" element={<div>detail page</div>} />
      </Routes>
    </MemoryRouter>,
  );
  return props;
}

describe("TagCloudModal", () => {
  it("renders the large cloud and the matching recordings list", () => {
    renderModal();
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Budget Planning" })).toBeTruthy();
    // No selection: both tagged recordings are listed with their durations.
    expect(screen.getByText("Budget call")).toBeTruthy();
    expect(screen.getByText("Vendor call")).toBeTruthy();
    expect(screen.getByText("1:01")).toBeTruthy(); // 61000 ms
  });

  it("clicking a tag forwards the shared selection to the parent", () => {
    const props = renderModal();
    fireEvent.click(screen.getByRole("button", { name: "Budget Planning" }));
    expect(props.onSelect).toHaveBeenCalledWith("Budget Planning");
  });

  it("with a selected tag, lists only its recordings", () => {
    renderModal({ selected: "Budget Planning" });
    expect(screen.getByText("Budget call")).toBeTruthy();
    expect(screen.queryByText("Vendor call")).toBeNull();
  });

  it("clicking a recording closes the modal and navigates to it", () => {
    const props = renderModal();
    fireEvent.click(screen.getByText("Budget call"));
    expect(props.onClose).toHaveBeenCalled();
    expect(screen.getByText("detail page")).toBeTruthy();
  });

  it("closes on Escape and on the close button, without navigating", () => {
    const props = renderModal();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(props.onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: /close/i }));
    expect(props.onClose).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("detail page")).toBeNull();
  });
});
