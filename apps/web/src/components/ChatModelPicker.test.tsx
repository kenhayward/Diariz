import { render, screen, fireEvent, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi } from "vitest";
import ChatModelPicker, { formatContext } from "./ChatModelPicker";
import type { ChatModelOption } from "../lib/types";

const MODELS: ChatModelOption[] = [
  {
    id: "a", label: "GPT OSS 20B", name: "openai/gpt-oss-20b", contextLength: 131072, isDefault: true,
    supportsImages: false, supportsTools: true, description: "Use this for most chats",
  },
  {
    id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false,
    supportsImages: true, supportsTools: false, description: null,
  },
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
  it("shows each model's context window as binary K, with the exact count on hover", () => {
    // "131,072 ctx" was accurate and unreadable: the longest thing on the row and the least glanceable.
    // 128K is the number the model's own documentation quotes.
    open();

    const row = screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ });
    expect(row.textContent).toContain("GPT OSS 20B");
    const chip = within(row).getByText("128K");
    expect(chip.getAttribute("title")).toBe("131,072 tokens");
    expect(row.textContent).not.toContain("131,072 ctx");
  });

  it("shows the administrator's description beside the name", () => {
    open();

    expect(screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ }).textContent)
      .toContain("Use this for most chats");
  });

  it("renders no description text for a model that has none", () => {
    // A model with no description gets empty flex space, not the word "null" and not a placeholder
    // sentence the platform made up.
    open();

    const row = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(row.textContent).not.toContain("null");
    expect(row.textContent).not.toContain("undefined");
  });

  it("titles the menu and explains its icons", () => {
    // The icons are the only thing on a row that is not words. Without the legend a briefcase is a guess,
    // and the menu had no title at all before.
    open();

    const menu = screen.getByRole("menu");
    expect(menu.textContent).toContain("Answering model");
    expect(menu.textContent).toContain("Calls tools");
    expect(menu.textContent).toContain("Reads images");
  });

  it("keeps the title and the legend fixed while only the rows scroll", () => {
    // A single scroll container over the whole menu would carry the legend out of sight exactly when a
    // long list makes it worth having.
    open();

    const menu = screen.getByRole("menu");
    const scroller = menu.querySelector("[data-testid='model-rows']");
    expect(scroller).not.toBeNull();
    expect(scroller!.className).toContain("overflow-y-auto");
    expect(menu.className).not.toContain("overflow-y-auto");
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

  it("renders the menu outside the panel that would clip it", () => {
    // The picker sits in a chat panel 260-640px wide whose scroll container computes overflow-x: auto.
    // A menu laid out inside that subtree hangs ~77px off its left edge (measured at the default 320px
    // width), and overflow to the LEFT of a scroll box is unreachable - scrollWidth equals clientWidth -
    // so the model names were simply invisible. The menu therefore has to escape the subtree entirely.
    const { container } = render(
      <ChatModelPicker models={MODELS} selectedId="a" onSelect={vi.fn()} />,
    );
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    const menu = screen.getByRole("menu");
    expect(container.contains(menu)).toBe(false);
    expect(document.body.contains(menu)).toBe(true);
  });

  it("selects a model from a real pointer sequence, not just a bare click", () => {
    // Once the menu is portalled it is no longer a descendant of the picker, so an outside-click check
    // that only knows about the picker would close it on the row's own mousedown and unmount the row
    // before its click ever landed. fireEvent.click cannot see that - it dispatches no mousedown.
    const onSelect = vi.fn();
    render(<ChatModelPicker models={MODELS} selectedId="a" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    fireEvent.mouseDown(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));
    fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));

    expect(onSelect).toHaveBeenCalledWith("b");
  });

  it("keeps the menu on screen when the anchor leaves no room to its left", () => {
    // The menu is right-aligned on the sparkle button. In a window narrow enough that the whole chat
    // panel is under ~300px from the left edge, aligning to the anchor alone would put the menu's left
    // edge off-screen - trading one invisible menu for another, so the placement has to push it back
    // to the 8px margin.
    render(<ChatModelPicker models={MODELS} selectedId="a" onSelect={vi.fn()} />);
    const button = screen.getByRole("button", { name: /model/i });
    button.getBoundingClientRect = () =>
      ({ bottom: 40, right: 290, left: 266, top: 16, width: 24, height: 24, x: 266, y: 16, toJSON: () => ({}) }) as DOMRect;
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 340 });

    fireEvent.click(button);

    const menu = screen.getByRole("menu");
    // At 372px the menu is wider than a 340px window, so it cannot be fully on screen and the guarantee
    // is about its LEFT edge only: the names live there, and losing the right edge costs at most the
    // context chip. Clamping the WIDTH instead would reintroduce the squeeze the overhang exists to
    // avoid, and this test asserted exactly that until the menu outgrew the window.
    const left = Number.parseFloat(menu.style.left);
    expect(left).toBe(8);
  });

  it("closes on Escape without selecting", () => {
    const onSelect = open();

    fireEvent.keyDown(document, { key: "Escape" });

    expect(screen.queryByRole("menuitemradio")).toBeNull();
    expect(onSelect).not.toHaveBeenCalled();
  });

  /// Without this the "Select a vision model" warning names a remedy the user cannot act on: nothing else
  /// in the product says which models can see. Tool support is asserted alongside it and independently,
  /// because they are separate resolved parameters and a model can have either, both, or neither -
  /// checking them together on one row would let a bug that ties them pass.
  it("marks tool support and image support independently", async () => {
    render(<ChatModelPicker models={MODELS} selectedId="a" onSelect={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /^Model:/ }));

    const oss = screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ });
    expect(within(oss).queryByRole("img", { name: "Calls tools" })).not.toBeNull();
    expect(within(oss).queryByRole("img", { name: "Reads images" })).toBeNull();

    const qwen = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(within(qwen).queryByRole("img", { name: "Calls tools" })).toBeNull();
    expect(within(qwen).queryByRole("img", { name: "Reads images" })).not.toBeNull();
  });
});

describe("formatContext", () => {
  it("rounds on 1024, not 1000", () => {
    // 131,072 is 128 binary K. Rounding on 1000 would print "131K", a number that matches nothing the
    // model's documentation says and that no one would recognise as its context window.
    expect(formatContext(131072)).toBe("128K");
    expect(formatContext(262144)).toBe("256K");
    expect(formatContext(8192)).toBe("8K");
  });

  it("switches to M at a megabyte of tokens", () => {
    expect(formatContext(1048576)).toBe("1M");
    expect(formatContext(1572864)).toBe("1.5M");
  });

  it("rounds an odd window to the nearest K rather than showing a fraction", () => {
    // Imported models routinely report a window that is not a power of two. The chip has room for three
    // or four characters, and the exact figure is one hover away.
    expect(formatContext(200000)).toBe("195K");
  });
});
