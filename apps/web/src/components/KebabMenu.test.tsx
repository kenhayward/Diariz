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
