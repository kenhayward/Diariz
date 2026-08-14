import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import TagsPopover from "./TagsPopover";

function setup(props: Partial<ComponentProps<typeof TagsPopover>> = {}) {
  const handlers = {
    onClose: vi.fn(),
    onAdd: vi.fn(),
    onRemove: vi.fn(),
    onDismiss: vi.fn(),
  };
  const view = render(
    <TagsPopover open tags={[]} suggested={[]} canEdit {...handlers} {...props} />,
  );
  /// Re-renders with a different `open`, the way the parent does. `RecordingTags` renders the popover
  /// unconditionally and only flips this prop, so the component stays mounted while closed.
  const setOpen = (open: boolean) =>
    view.rerender(
      <TagsPopover open={open} tags={[]} suggested={[]} canEdit {...handlers} {...props} />,
    );
  return { ...handlers, setOpen };
}

const field = () => screen.getByLabelText("Add a tag") as HTMLInputElement;

describe("TagsPopover", () => {
  it("commits a word on space and keeps the field focused for the next one", async () => {
    const { onAdd } = setup();
    const input = screen.getByLabelText("Add a tag");

    await userEvent.type(input, "metadata ");

    expect(onAdd).toHaveBeenCalledWith("metadata");
    expect(document.activeElement).toBe(input);
    expect((input as HTMLInputElement).value).toBe("");
  });

  it("commits on Enter and closes, because Enter means done", async () => {
    const { onAdd, onClose } = setup();

    await userEvent.type(screen.getByLabelText("Add a tag"), "metadata{Enter}");

    expect(onAdd).toHaveBeenCalledWith("metadata");
    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Enter with an empty field without adding anything", async () => {
    const { onAdd, onClose } = setup();

    await userEvent.type(screen.getByLabelText("Add a tag"), "{Enter}");

    expect(onAdd).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("does not add a tag it already has", async () => {
    const { onAdd } = setup({ tags: ["metadata"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "METADATA ");

    expect(onAdd).not.toHaveBeenCalled();
  });

  it("joins a pasted phrase with hyphens", async () => {
    const { onAdd } = setup();
    const input = screen.getByLabelText("Add a tag");

    await userEvent.click(input);
    await userEvent.paste("budget planning 2026");

    expect(onAdd).toHaveBeenCalledWith("budget-planning-2026");
  });

  it("keeps what was already typed when text is pasted onto it", async () => {
    // The paste handler commits instead of letting the field take the text, so it has to account for the
    // draft the caret is sitting in - otherwise typing "budg" then pasting "et planning" loses the prefix.
    const { onAdd } = setup();

    await userEvent.type(field(), "budg");
    await userEvent.paste("et planning");

    expect(onAdd).toHaveBeenCalledWith("budget-planning");
  });

  it("pastes into the middle of the draft, where the caret is", async () => {
    const { onAdd } = setup();

    await userEvent.type(field(), "bud-ning");
    field().setSelectionRange(4, 4);
    await userEvent.paste("plan");

    expect(onAdd).toHaveBeenCalledWith("bud-planning");
  });

  it("forgets a draft that was abandoned instead of committing it later", async () => {
    // The popover stays mounted while closed (HubPopover renders null, the component keeps its state), so a
    // draft the user walked away from used to survive - and the next Enter, days of scrolling later, adopted
    // a half-typed word nobody asked for.
    const { onAdd, setOpen } = setup();

    await userEvent.type(field(), "budg");
    setOpen(false); // Escape, or a click outside
    expect(onAdd).not.toHaveBeenCalled(); // closing is not a commit

    setOpen(true);
    expect(field().value).toBe("");

    await userEvent.type(field(), "{Enter}");
    expect(onAdd).not.toHaveBeenCalled(); // and the abandoned word cannot be committed after the fact
  });

  it("removes the last tag on Backspace in an empty field", async () => {
    const { onRemove } = setup({ tags: ["first", "last"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "{Backspace}");

    expect(onRemove).toHaveBeenCalledWith("last");
  });

  it("leaves the tags alone when Backspace edits the draft", async () => {
    const { onRemove } = setup({ tags: ["first"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "ab{Backspace}");

    expect(onRemove).not.toHaveBeenCalled();
  });

  it("removes a tag from its chip", async () => {
    const { onRemove } = setup({ tags: ["metadata", "licensing"] });

    await userEvent.click(screen.getAllByRole("button", { name: "Remove tag" })[0]);

    expect(onRemove).toHaveBeenCalledWith("metadata");
  });

  it("promotes a suggestion when its label is clicked", async () => {
    const { onAdd } = setup({ suggested: ["templates", "document-map"] });

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    expect(onAdd).toHaveBeenCalledWith("templates");
  });

  it("dismisses a suggestion from its own control", async () => {
    const { onDismiss } = setup({ suggested: ["templates"] });

    await userEvent.click(screen.getByRole("button", { name: "Never suggest this" }));

    expect(onDismiss).toHaveBeenCalledWith("templates");
  });

  it("counts the suggestions still to deal with", () => {
    setup({ suggested: ["a", "b", "c"] });
    expect(screen.getByText("3 left")).toBeTruthy();
  });

  it("says so when every suggestion has been dealt with", () => {
    setup({ suggested: [] });
    expect(screen.getByText("All suggestions dealt with.")).toBeTruthy();
  });

  it("renders nothing when closed", () => {
    setup({ open: false });
    expect(screen.queryByLabelText("Add a tag")).toBeNull();
  });

  describe("read-only mode (canEdit=false)", () => {
    it("shows the adopted tags as plain text, with no remove button", () => {
      setup({ canEdit: false, tags: ["metadata", "licensing"] });

      expect(screen.getByText("metadata")).toBeTruthy();
      expect(screen.getByText("licensing")).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Remove tag" })).toBeNull();
    });

    it("renders no entry field and no input hint", () => {
      setup({ canEdit: false, tags: ["metadata"] });

      expect(screen.queryByLabelText("Add a tag")).toBeNull();
      expect(screen.queryByText(/Space or Enter adds the word/)).toBeNull();
    });

    it("hides the suggestions section entirely, even with suggestions present", () => {
      setup({ canEdit: false, suggested: ["templates", "document-map"] });

      expect(screen.queryByText("AUTO-GENERATED · PICK OR IGNORE")).toBeNull();
      expect(screen.queryByText("templates")).toBeNull();
      expect(screen.queryByText("document-map")).toBeNull();
      expect(screen.queryByText("2 left")).toBeNull();
      expect(screen.queryByText("All suggestions dealt with.")).toBeNull();
    });

    it("shows the view-only note instead of the saved-as-you-type subtitle", () => {
      setup({ canEdit: false });

      expect(screen.getByText("view only - you cannot change these tags")).toBeTruthy();
      expect(screen.queryByText("saved as you type")).toBeNull();
    });

    it("keeps the header title and close button, and the close button still works", async () => {
      const { onClose } = setup({ canEdit: false });

      expect(screen.getByText("Tags")).toBeTruthy();
      await userEvent.click(screen.getByLabelText("Close"));

      expect(onClose).toHaveBeenCalled();
    });
  });
});
