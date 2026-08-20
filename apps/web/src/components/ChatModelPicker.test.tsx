import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi } from "vitest";
import ChatModelPicker from "./ChatModelPicker";
import type { ChatModelOption } from "../lib/types";

const MODELS: ChatModelOption[] = [
  { id: "a", label: "GPT OSS 20B", name: "openai/gpt-oss-20b", contextLength: 131072, isDefault: true },
  { id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false },
];

function open(props: Partial<React.ComponentProps<typeof ChatModelPicker>> = {}) {
  const onSelect = props.onSelect ?? vi.fn();
  render(
    <ChatModelPicker models={MODELS} selectedId="a" onSelect={onSelect} {...props} />,
  );
  fireEvent.click(screen.getByRole("button", { name: /model/i }));
  return onSelect;
}

describe("ChatModelPicker", () => {
  it("lists each model's label with its context length in brackets", () => {
    open();

    const row = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(row.textContent).toContain("QWEN 3.8");
    expect(row.textContent).toContain("(200,000 ctx)");
  });

  it("never shows the raw slug", () => {
    // The slug is what the endpoint needs, not what a person choosing a model should have to read.
    open();

    expect(screen.queryByText(/qwen3\.8-27b@q4_k_xl/)).toBeNull();
    expect(screen.queryByText(/openai\/gpt-oss-20b/)).toBeNull();
  });

  it("reports the chosen model and closes", () => {
    const onSelect = open();

    fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));

    expect(onSelect).toHaveBeenCalledWith("b");
    expect(screen.queryByRole("menuitemradio")).toBeNull();
  });

  it("marks the current selection", () => {
    open({ selectedId: "b" });

    expect(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ }).getAttribute("aria-checked")).toBe("false");
  });

  it("falls back to the default when the selection is no longer offered", () => {
    // An administrator can un-tick a model between a conversation being saved and reopened. Showing the
    // stale id as selected would claim a model that is not in the list.
    open({ selectedId: "gone" });

    expect(screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ }).getAttribute("aria-checked")).toBe("true");
  });

  it("does not open while a reply is streaming", async () => {
    // Switching mid-stream would change the model behind an answer already arriving; the turn is in
    // flight with the old one either way.
    const onSelect = vi.fn();
    render(<ChatModelPicker models={MODELS} selectedId="a" disabled onSelect={onSelect} />);

    await userEvent.click(screen.getByRole("button", { name: /model/i }), { pointerEventsCheck: 0 });

    expect(screen.queryByRole("menuitemradio")).toBeNull();
  });

  it("renders even with a single model", () => {
    // Always visible, so the affordance is discoverable and the toolbar's layout never shifts as an
    // administrator adds or removes models.
    render(<ChatModelPicker models={[MODELS[0]]} selectedId="a" onSelect={vi.fn()} />);

    expect(screen.getByRole("button", { name: /model/i })).toBeTruthy();
  });

  it("names the current model on the button, so it reads without opening", () => {
    render(<ChatModelPicker models={MODELS} selectedId="b" onSelect={vi.fn()} />);

    expect(screen.getByRole("button", { name: /QWEN 3\.8/ })).toBeTruthy();
  });

  it("keeps the full name reachable when a long one is shortened", () => {
    // Imported models carry the endpoint's own slug, which is routinely too long for the menu. The row
    // ellipsises rather than overflowing, so the whole name has to stay available somewhere.
    open();

    const row = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(row.getAttribute("title")).toBe("QWEN 3.8");
  });

  it("closes on Escape without selecting", () => {
    const onSelect = open();

    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByRole("menuitemradio")).toBeNull();
    expect(onSelect).not.toHaveBeenCalled();
  });
});
