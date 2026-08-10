import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import TabStrip, { TABPANEL_ID, tabId } from "./TabStrip";

describe("TabStrip", () => {
  // The tablist previously had no accessible name at all - a screen-reader user landed on an unnamed group
  // of four tabs with no context for what they switch.
  it("names the tablist", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    expect(screen.getByRole("tablist").getAttribute("aria-label")).toBeTruthy();
  });

  // Each tab needs a stable id + aria-controls pointing at the (single, swapped-in-place) tab body, so the
  // body can point aria-labelledby back at whichever tab is active. Without this, activating a tab announces
  // a selection change with no panel for the pattern to actually connect to.
  it("wires every tab to the shared tabpanel id via aria-controls, with a stable per-tab id", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    for (const key of ["list", "calendar", "actions", "tags"] as const) {
      const label = { list: "List", calendar: "Calendar", actions: "Actions", tags: "Tags" }[key];
      const el = screen.getByRole("tab", { name: label });
      expect(el.id).toBe(tabId(key));
      expect(el.getAttribute("aria-controls")).toBe(TABPANEL_ID);
    }
  });

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
