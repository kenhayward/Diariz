import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import KebabMenu from "./KebabMenu";

describe("KebabMenu", () => {
  it("opens on click, runs an action, and closes", () => {
    const onClick = vi.fn();
    render(<KebabMenu actions={[{ label: "Delete", onClick, danger: true }]} />);

    // Closed initially.
    expect(screen.queryByRole("menuitem", { name: "Delete" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /actions/i }));
    const item = screen.getByRole("menuitem", { name: "Delete" });

    fireEvent.click(item);
    expect(onClick).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menuitem", { name: "Delete" })).toBeNull(); // closed again
  });

  /// A menu on the last row of a scrolling panel would otherwise open into the panel's bottom edge and be
  /// cut off - which is what happened to the Automations card, the last thing on the Integrations page.
  it("opens upward when there is no room below the trigger", () => {
    render(<KebabMenu actions={[{ label: "Delete", onClick: vi.fn() }]} />);
    const trigger = screen.getByRole("button", { name: /actions/i });
    // jsdom has no layout, so the trigger says where it is: near the bottom of the viewport.
    vi.spyOn(trigger, "getBoundingClientRect").mockReturnValue({ bottom: window.innerHeight - 20 } as DOMRect);

    fireEvent.click(trigger);

    expect(screen.getByRole("menu").className).toContain("bottom-full");
  });

  it("opens downward when there is room", () => {
    render(<KebabMenu actions={[{ label: "Delete", onClick: vi.fn() }]} />);
    const trigger = screen.getByRole("button", { name: /actions/i });
    vi.spyOn(trigger, "getBoundingClientRect").mockReturnValue({ bottom: 10 } as DOMRect);

    fireEvent.click(trigger);

    expect(screen.getByRole("menu").className).not.toContain("bottom-full");
  });

  it("closes on Escape without running an action", () => {
    const onClick = vi.fn();
    render(<KebabMenu actions={[{ label: "Rename", onClick }]} />);

    fireEvent.click(screen.getByRole("button", { name: /actions/i }));
    expect(screen.getByRole("menuitem", { name: "Rename" })).toBeTruthy();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("menuitem", { name: "Rename" })).toBeNull();
    expect(onClick).not.toHaveBeenCalled();
  });
});
