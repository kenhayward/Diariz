import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi } from "vitest";
import FolderPickerModal from "./FolderPickerModal";

const sections = [
  { id: "customers", name: "Customers", parentId: null, position: 0 },
  { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
];

function renderPicker(props: Partial<Parameters<typeof FolderPickerModal>[0]> = {}) {
  const onSelect = vi.fn();
  const onClose = vi.fn();
  render(
    <FolderPickerModal
      sections={sections}
      selectedId={props.selectedId ?? null}
      onSelect={props.onSelect ?? onSelect}
      onClose={props.onClose ?? onClose}
    />,
  );
  return { onSelect, onClose };
}

describe("FolderPickerModal", () => {
  it("names itself and says what the choice is for", () => {
    renderPicker();
    expect(screen.getByRole("dialog", { name: "Choose a folder" })).toBeTruthy();
    expect(screen.getByText("Every new recording will be filed here.")).toBeTruthy();
  });

  it("puts the caret in the filter box on open, so typing works without a click", () => {
    renderPicker();
    expect(document.activeElement).toBe(screen.getByLabelText("Filter folders"));
  });

  it("shows the chosen folder's full path, and Ungrouped for the root", () => {
    const { rerender } = render(
      <FolderPickerModal sections={sections} selectedId="acme" onSelect={() => {}} onClose={() => {}} />,
    );
    expect(screen.getByText(/Chosen:/).textContent).toContain("Customers › Acme Corp");

    rerender(<FolderPickerModal sections={sections} selectedId={null} onSelect={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/Chosen:/).textContent).toContain("Ungrouped");
  });

  it("closes on Done and on the close control", () => {
    const a = renderPicker();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(a.onClose).toHaveBeenCalled();
    // The Done click above does not itself unmount anything - a real caller would react to `onClose` by
    // dropping the dialog, but this mock does not, so the previous instance would otherwise linger
    // alongside the next `render()` below and shadow its "Close folder picker" button.
    cleanup();

    const b = renderPicker();
    fireEvent.click(screen.getAllByRole("button", { name: "Close folder picker" })[0]);
    expect(b.onClose).toHaveBeenCalled();
  });

  // The Preferences modal listens for Escape on `document`. If this dialog let Escape through, one press
  // would close both, throwing the user out of Preferences to dismiss a picker.
  it("swallows Escape so an enclosing modal does not close too", () => {
    const outer = vi.fn();
    document.addEventListener("keydown", outer);
    try {
      const { onClose } = renderPicker();
      fireEvent.keyDown(screen.getByLabelText("Filter folders"), { key: "Escape" });
      expect(onClose).toHaveBeenCalled();
      expect(outer).not.toHaveBeenCalled();
    } finally {
      document.removeEventListener("keydown", outer);
    }
  });

  it("lets a non-empty filter take Escape for itself, closing nothing", () => {
    const { onClose } = renderPicker();
    const filter = screen.getByLabelText("Filter folders");
    fireEvent.change(filter, { target: { value: "acme" } });
    fireEvent.keyDown(filter, { key: "Escape" });
    expect((filter as HTMLInputElement).value).toBe("");
    expect(onClose).not.toHaveBeenCalled();
  });

  it("passes a choice straight through - it is applied to the panel, not held here", () => {
    const { onSelect } = renderPicker();
    fireEvent.click(screen.getByLabelText("Select Ungrouped"));
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  // Moved here from RecordingsSection.test.tsx, where the picker used to be inline. Real Tab presses
  // only, no `.focus()` shortcut - this proves the whole chain is reachable by keyboard, not merely that
  // each target is focusable. `FolderPicker` costs 2 stops per drillable row by design; the point of this
  // test is that the dialog chrome does not break that chain.
  it("is keyboard operable from the filter box through to a folder row", async () => {
    const user = userEvent.setup();
    const { onSelect } = renderPicker();

    expect(document.activeElement).toBe(screen.getByLabelText("Filter folders"));
    await user.tab(); // the root "Ungrouped" row
    expect(document.activeElement).toBe(screen.getByLabelText("Select Ungrouped"));
    await user.tab(); // "Customers" row body (drills, does not choose)
    expect(document.activeElement).toBe(screen.getByLabelText("Open Customers"));
    await user.tab(); // "Customers" row's separate select control
    expect(document.activeElement).toBe(screen.getByLabelText("Select Customers"));

    await user.keyboard("{Enter}");
    expect(onSelect).toHaveBeenCalledWith("customers");
  });
});
