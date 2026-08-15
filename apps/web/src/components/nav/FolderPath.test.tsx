import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FolderPath from "./FolderPath";
import type { PathCrumb } from "../../lib/folderPath";

const crumbs: PathCrumb[] = [
  { id: "customers", name: "Customers" },
  { id: "acme", name: "Acme Corp" },
  { id: "falcon", name: "Project Falcon" },
  { id: "phase2", name: "Phase 2" },
];

describe("FolderPath", () => {
  it("renders a short path in full", async () => {
    render(<FolderPath crumbs={crumbs.slice(0, 2)} maxVisible={3} />);
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Acme Corp")).toBeTruthy();
  });

  it("collapses the middle of a long path but keeps the current folder", () => {
    render(<FolderPath crumbs={crumbs} maxVisible={2} />);
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Phase 2")).toBeTruthy();
    expect(screen.queryByText("Acme Corp")).toBeNull();
  });

  it("calls onSelect with the crumb id when a crumb is clicked", async () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={4} onSelect={onSelect} />);

    await userEvent.click(screen.getByText("Acme Corp"));

    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("renders the current (last) crumb as static text with aria-current, not a button", () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={4} onSelect={onSelect} />);

    // Not a clickable control - a history entry that pops nothing on Back would be a broken affordance.
    expect(screen.queryByRole("button", { name: "Phase 2" })).toBeNull();
    const current = screen.getByText("Phase 2");
    expect(current.getAttribute("aria-current")).toBe("page");

    // The other crumbs stay real buttons.
    expect(screen.getByRole("button", { name: "Acme Corp" })).toBeTruthy();
  });

  it("lists every ancestor in the menu, including ones collapsed out of the path", async () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={2} onSelect={onSelect} />);

    await userEvent.click(screen.getByLabelText("Show full folder path"));

    // Acme Corp is hidden from the path but must be reachable from the menu.
    await userEvent.click(screen.getByRole("menuitem", { name: "Acme Corp" }));
    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  // The slot exists so a caller can put a control immediately left of the menu trigger. Order is the
  // whole point of the prop, so it is asserted as DOM order, not merely presence.
  it("renders trailingAction between the path and the menu trigger", () => {
    const { container } = render(
      <FolderPath crumbs={crumbs} trailingAction={<button type="button">Page</button>} />,
    );

    const children = Array.from(container.firstElementChild!.children);
    expect(children.indexOf(screen.getByRole("navigation"))).toBe(0);
    expect(children.indexOf(screen.getByRole("button", { name: "Page" }))).toBe(1);
    expect(children.indexOf(screen.getByLabelText("Show full folder path"))).toBe(2);
  });

  it("renders no trailing slot when no action is given", () => {
    const { container } = render(<FolderPath crumbs={crumbs} />);

    // Just the nav and the menu trigger - an empty slot must not leave a stray element behind.
    expect(container.firstElementChild!.children.length).toBe(2);
  });

  it("calls onCrumbDrop with the crumb id and the dropped recording id", () => {
    const onCrumbDrop = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={4} onSelect={vi.fn()} onCrumbDrop={onCrumbDrop} />);

    // "Acme Corp" is a non-terminal crumb - a recording dragged onto it should move up to that level.
    fireEvent.drop(screen.getByText("Acme Corp"), { dataTransfer: { getData: () => "rec-1" } });

    expect(onCrumbDrop).toHaveBeenCalledWith("acme", "rec-1");
  });

  it("uses label/menuLabel to override the default accessible names", () => {
    render(<FolderPath crumbs={crumbs} label="This folder's path" menuLabel="Show full path for this folder" />);

    expect(screen.getByRole("navigation", { name: "This folder's path" })).toBeTruthy();
    expect(screen.getByLabelText("Show full path for this folder")).toBeTruthy();
    // The generic defaults must not also be present - two instances on one screen must not collide.
    expect(screen.queryByRole("navigation", { name: "Folder path" })).toBeNull();
  });
});
