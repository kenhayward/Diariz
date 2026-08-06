import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi } from "vitest";
import FolderPicker from "./FolderPicker";
import type { SectionDto } from "../lib/types";

const section = (id: string, name: string, parentId: string | null = null): SectionDto => ({
  id,
  name,
  parentId,
  position: 0,
});

// Two top-level folders, one with a deep chain underneath it, so a filter test can prove it searches the
// whole tree rather than only the current drill level. "Personal" also has its own descendant
// ("Old Notes") so a test can drill into "Customers" and filter for something that exists only under the
// *sibling* branch - the case a subtree-scoped (rather than level-scoped) filter bug would still pass.
const sections: SectionDto[] = [
  section("customers", "Customers"),
  section("acme", "Acme Corp", "customers"),
  section("falcon", "Project Falcon", "acme"),
  section("phase2", "Phase 2", "falcon"),
  section("personal", "Personal"),
  section("archive", "Archive", "personal"),
  section("oldnotes", "Old Notes", "archive"),
];

function renderPicker(opts: { selectedId?: string | null; onSelect?: (id: string | null) => void } = {}) {
  const onSelect = opts.onSelect ?? vi.fn();
  render(<FolderPicker sections={sections} selectedId={opts.selectedId ?? undefined ?? null} onSelect={onSelect} />);
  return onSelect;
}

describe("FolderPicker", () => {
  it("shows only the top-level folders at the root drill position", () => {
    renderPicker();
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Personal")).toBeTruthy();
    // A descendant several levels down must not leak into the top level.
    expect(screen.queryByText("Project Falcon")).toBeNull();
  });

  it("drills into a folder and shows its children", async () => {
    renderPicker();
    await userEvent.click(screen.getByLabelText("Open Customers"));
    expect(screen.getByText("Acme Corp")).toBeTruthy();
    expect(screen.queryByText("Personal")).toBeNull();
  });

  it("filtering matches folders anywhere in the tree, not just the current level, and shows each with its full path", async () => {
    renderPicker();
    // Drilled somewhere unrelated to the match, to prove the search isn't scoped to the current level.
    await userEvent.click(screen.getByLabelText("Open Customers"));

    await userEvent.type(screen.getByLabelText("Filter folders"), "Phase");

    expect(screen.getByText("Customers › Acme Corp › Project Falcon › Phase 2")).toBeTruthy();
    // The intervening levels are not shown as separate rows in filter mode, only the match with its path.
    expect(screen.queryByText("Acme Corp")).toBeNull();
  });

  it("filtering finds a match under a sibling branch of the drilled-into folder, not just its own subtree", async () => {
    renderPicker();
    // Drilled into "Customers" - "Old Notes" lives under "Personal", a completely different top-level
    // branch. A filter scoped to the drilled folder's own subtree (rather than the whole tree) would
    // still miss this, unlike the level-scoped bug the other filter test rules out.
    await userEvent.click(screen.getByLabelText("Open Customers"));

    await userEvent.type(screen.getByLabelText("Filter folders"), "Old Notes");

    expect(screen.getByText("Personal › Archive › Old Notes")).toBeTruthy();
    expect(screen.queryByText("Acme Corp")).toBeNull();
  });

  it("clearing the filter returns to the drill position it was showing before, not the root", async () => {
    renderPicker();
    await userEvent.click(screen.getByLabelText("Open Customers"));
    expect(screen.getByText("Acme Corp")).toBeTruthy();

    const filterBox = screen.getByLabelText("Filter folders");
    await userEvent.type(filterBox, "Phase");
    expect(screen.getByText("Customers › Acme Corp › Project Falcon › Phase 2")).toBeTruthy();

    await userEvent.clear(filterBox);

    // Back to Customers' children, not the top level and not stuck on the filtered view.
    expect(screen.getByText("Acme Corp")).toBeTruthy();
    expect(screen.queryByText("Personal")).toBeNull();
  });

  it("does not offer a drill target while filtering - the point of filtering is to skip drilling", async () => {
    renderPicker();
    await userEvent.type(screen.getByLabelText("Filter folders"), "Acme");
    expect(screen.queryByLabelText("Open Acme Corp")).toBeNull();
  });

  it("calls onSelect with a folder's id when it is chosen", async () => {
    const onSelect = renderPicker();
    await userEvent.click(screen.getByLabelText("Select Customers"));
    expect(onSelect).toHaveBeenCalledWith("customers");
  });

  it("calls onSelect with a folder's id when chosen from filtered results", async () => {
    const onSelect = renderPicker();
    await userEvent.type(screen.getByLabelText("Filter folders"), "Acme");
    // The accessible name mirrors the visible path (not just the bare name), the same disambiguation two
    // same-named folders in different branches would need.
    await userEvent.click(screen.getByLabelText("Select Customers › Acme Corp"));
    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("offers the root as a selectable row and calls onSelect with null when chosen", async () => {
    const onSelect = renderPicker();
    await userEvent.click(screen.getByLabelText("Select Ungrouped"));
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  it("marks the currently selected folder", () => {
    renderPicker({ selectedId: "customers" });
    expect(screen.getByLabelText("Select Customers").getAttribute("aria-current")).toBe("true");
    expect(screen.getByLabelText("Select Personal").getAttribute("aria-current")).toBeNull();
  });

  it("marks the root as selected when selectedId is null", () => {
    renderPicker({ selectedId: null });
    expect(screen.getByLabelText("Select Ungrouped").getAttribute("aria-current")).toBe("true");
  });

  it("shows a no-matches message rather than an empty list when nothing matches the filter", async () => {
    renderPicker();
    await userEvent.type(screen.getByLabelText("Filter folders"), "nonexistent-folder-xyz");
    expect(screen.getByText("No folders match your search.")).toBeTruthy();
  });

  it("labels the filter box and the folder list for assistive tech", () => {
    renderPicker();
    expect(screen.getByRole("textbox", { name: "Filter folders" })).toBeTruthy();
    expect(screen.getByRole("list", { name: "Folders" })).toBeTruthy();
  });

  it("is operable by keyboard alone", async () => {
    const onSelect = renderPicker();
    await userEvent.tab(); // focuses the filter box first
    await userEvent.tab(); // moves on to the first interactive row
    // Whatever is focused next responds to Enter like a real button; find the root's select control and
    // drive it by keyboard directly rather than assuming tab order, since that would be brittle here.
    screen.getByLabelText("Select Ungrouped").focus();
    await userEvent.keyboard("{Enter}");
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  describe("persisted selection stays visible (regression: a <select> always shows its value)", () => {
    it("shows the persisted selection's full path when its row is not part of the current view", () => {
      // "Phase 2" lives four levels down (Customers > Acme Corp > Project Falcon > Phase 2). At the root
      // drill position its row is not rendered at all, so nothing would otherwise indicate it is chosen.
      renderPicker({ selectedId: "phase2" });
      expect(screen.getByText("Selected: Customers › Acme Corp › Project Falcon › Phase 2")).toBeTruthy();
    });

    it("does not show the notice when the selected folder's row is already visible", () => {
      renderPicker({ selectedId: "customers" });
      expect(screen.queryByText(/^Selected: /)).toBeNull();
    });

    it("does not show the notice for the root selection - the Ungrouped row is always shown and marked", () => {
      renderPicker({ selectedId: null });
      expect(screen.queryByText(/^Selected: /)).toBeNull();
    });

    it("stops showing the notice once you drill down to where the selection becomes visible", async () => {
      renderPicker({ selectedId: "phase2" });
      expect(screen.getByText(/^Selected: /)).toBeTruthy();

      await userEvent.click(screen.getByLabelText("Open Customers"));
      await userEvent.click(screen.getByLabelText("Open Acme Corp"));
      await userEvent.click(screen.getByLabelText("Open Project Falcon"));

      expect(screen.queryByText(/^Selected: /)).toBeNull();
      expect(screen.getByLabelText("Select Phase 2").getAttribute("aria-current")).toBe("true");
    });
  });
});
