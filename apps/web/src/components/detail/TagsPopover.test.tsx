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
  render(
    <TagsPopover open tags={[]} suggested={[]} {...handlers} {...props} />,
  );
  return handlers;
}

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
});
