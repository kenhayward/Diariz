import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FolderChips from "./FolderChips";

const CRUMBS = [
  { id: "cust", name: "Customers" },
  { id: "acme", name: "Acme Corp" },
  { id: "falcon", name: "Project Falcon" },
];

describe("FolderChips", () => {
  it("renders the room first, then a chip per folder", () => {
    render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={vi.fn()} />);

    const chips = screen.getAllByRole("button").map((b) => b.textContent);
    expect(chips).toEqual(["Personal", "Customers", "Acme Corp", "Project Falcon"]);
  });

  /// Without it the row reads as generic tags. The icon is the same one the recordings panel puts before
  /// its breadcrumb, so the two views say "these are folders" the same way.
  it("leads with a folder icon, before the first chip", () => {
    const { container } = render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={vi.fn()} />);

    const nav = container.querySelector("nav")!;
    const icon = nav.querySelector("svg")!;
    const firstChip = nav.querySelector("button")!;

    expect(icon).not.toBeNull();
    // DOCUMENT_POSITION_FOLLOWING: the first chip comes after the icon.
    expect(icon.compareDocumentPosition(firstChip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  /// The detail page renders the icon itself, as the Change folder button. Two adjacent folder glyphs would
  /// blur which control is which.
  it("can omit its leading folder icon", () => {
    // Both halves, in one test: on its own, "the icon is absent when showIcon is false" would also pass if
    // the test id never existed at all - it would be asserting nothing.
    const { unmount } = render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={vi.fn()} />);
    expect(screen.getByTestId("folder-chips-icon")).toBeTruthy();
    unmount();

    render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={vi.fn()} showIcon={false} />);

    expect(screen.queryByTestId("folder-chips-icon")).toBeNull();
    // The chips themselves are untouched - only the glyph goes.
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual([
      "Personal",
      "Customers",
      "Acme Corp",
      "Project Falcon",
    ]);
  });

  it("reports the folder id when a folder chip is clicked", async () => {
    const onSelect = vi.fn();
    render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={onSelect} />);

    await userEvent.click(screen.getByRole("button", { name: "Acme Corp" }));

    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("reports null when the room chip is clicked", async () => {
    const onSelect = vi.fn();
    render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={onSelect} />);

    await userEvent.click(screen.getByRole("button", { name: "Personal" }));

    expect(onSelect).toHaveBeenCalledWith(null);
  });

  /// Unlike the nav's breadcrumb, the LAST chip is a live control here: it names the folder the recording is
  /// filed in, and the reader is on the recording page, not in that folder - so clicking it has somewhere to
  /// go. Making it static (the nav's rule) would break the most likely click of the row.
  it("keeps the deepest folder clickable", async () => {
    const onSelect = vi.fn();
    render(<FolderChips roomName="Personal" crumbs={CRUMBS} onSelect={onSelect} />);

    await userEvent.click(screen.getByRole("button", { name: "Project Falcon" }));

    expect(onSelect).toHaveBeenCalledWith("falcon");
  });

  it("shows only the room chip for a recording filed at the top level", () => {
    render(<FolderChips roomName="Personal" crumbs={[]} onSelect={vi.fn()} />);

    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Personal"]);
  });

  /// Folders nest to 8, so a deep path has to give way rather than push the hero wide. The room anchor and
  /// the tail survive; the middle collapses - the same rule the nav uses.
  it("collapses the middle of a deep path, keeping the room and the tail", () => {
    const deep = [
      { id: "a", name: "A" },
      { id: "b", name: "B" },
      { id: "c", name: "C" },
      { id: "d", name: "D" },
      { id: "e", name: "E" },
    ];
    render(<FolderChips roomName="Personal" crumbs={deep} onSelect={vi.fn()} maxVisible={3} />);

    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Personal", "A", "D", "E"]);
    expect(screen.queryByText("…")).not.toBeNull();
  });

  it("does not make the collapsed marker clickable", () => {
    const deep = [
      { id: "a", name: "A" },
      { id: "b", name: "B" },
      { id: "c", name: "C" },
      { id: "d", name: "D" },
    ];
    render(<FolderChips roomName="Personal" crumbs={deep} onSelect={vi.fn()} maxVisible={2} />);

    expect(screen.queryByRole("button", { name: "…" })).toBeNull();
  });
});
