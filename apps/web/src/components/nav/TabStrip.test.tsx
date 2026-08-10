import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import TabStrip from "./TabStrip";

describe("TabStrip", () => {
  it("renders the four panel tabs in a tablist", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    expect(screen.getByRole("tablist")).toBeTruthy();
    expect(screen.getAllByRole("tab").map((el) => el.textContent)).toEqual([
      "List",
      "Calendar",
      "Actions",
      "Tags",
    ]);
  });

  it("marks only the active tab as selected", () => {
    render(<TabStrip tab="actions" onSelect={() => {}} />);
    expect(screen.getByRole("tab", { name: "Actions" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "List" }).getAttribute("aria-selected")).toBe("false");
  });

  it("reports the picked tab", () => {
    const onSelect = vi.fn();
    render(<TabStrip tab="list" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole("tab", { name: "Calendar" }));
    expect(onSelect).toHaveBeenCalledWith("calendar");
  });

  // The strip is a row above the content now, not a rail beside it: it must not carry the vertical
  // writing mode that made the old rail's labels read bottom-to-top.
  it("lays the tabs out horizontally", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    expect(screen.getByRole("tablist").className).toContain("flex");
    expect(screen.getByRole("tab", { name: "List" }).className).not.toContain("writing-mode");
  });
});
