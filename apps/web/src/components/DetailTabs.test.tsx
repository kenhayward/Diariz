import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import DetailTabs, { type DetailTab } from "./DetailTabs";

const tabs: DetailTab[] = [
  { key: "one", label: "One", toolbar: <button>tool-one</button>, content: <p>content one</p> },
  { key: "two", label: "Two", toolbar: <button>tool-two</button>, content: <p>content two</p> },
  { key: "bare", label: "Bare", content: <p>content bare</p> }, // no toolbar
];

describe("DetailTabs", () => {
  it("renders a tab per entry with the active one selected", () => {
    render(<DetailTabs tabs={tabs} active="one" onSelect={vi.fn()} />);
    expect(screen.getAllByRole("tab")).toHaveLength(3);
    expect(screen.getByRole("tab", { name: "One" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Two" }).getAttribute("aria-selected")).toBe("false");
  });

  it("shows only the active tab's toolbar and content", () => {
    render(<DetailTabs tabs={tabs} active="one" onSelect={vi.fn()} />);
    expect(screen.getByText("content one")).toBeTruthy();
    expect(screen.getByText("tool-one")).toBeTruthy();
    expect(screen.queryByText("content two")).toBeNull();
    expect(screen.queryByText("tool-two")).toBeNull();
  });

  it("calls onSelect with the tab key when a tab is clicked", () => {
    const onSelect = vi.fn();
    render(<DetailTabs tabs={tabs} active="one" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole("tab", { name: "Two" }));
    expect(onSelect).toHaveBeenCalledWith("two");
  });

  it("falls back to the first tab when active is unknown", () => {
    render(<DetailTabs tabs={tabs} active="nope" onSelect={vi.fn()} />);
    expect(screen.getByText("content one")).toBeTruthy();
  });

  it("renders a tab that has no toolbar without error", () => {
    render(<DetailTabs tabs={tabs} active="bare" onSelect={vi.fn()} />);
    expect(screen.getByText("content bare")).toBeTruthy();
  });
});
