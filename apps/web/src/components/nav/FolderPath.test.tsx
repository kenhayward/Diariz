import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
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

  it("lists every ancestor in the menu, including ones collapsed out of the path", async () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={2} onSelect={onSelect} />);

    await userEvent.click(screen.getByLabelText("Show full folder path"));

    // Acme Corp is hidden from the path but must be reachable from the menu.
    await userEvent.click(screen.getByRole("menuitem", { name: "Acme Corp" }));
    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("puts extra items at the top of the menu", async () => {
    const onClick = vi.fn();
    render(<FolderPath crumbs={crumbs} extraItems={[{ label: "Open section page", onClick }]} />);

    await userEvent.click(screen.getByLabelText("Show full folder path"));
    await userEvent.click(screen.getByRole("menuitem", { name: "Open section page" }));

    expect(onClick).toHaveBeenCalled();
  });
});
