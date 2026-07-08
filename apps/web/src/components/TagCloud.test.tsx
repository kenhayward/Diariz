import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import TagCloud from "./TagCloud";
import type { TagCloudEntry } from "../lib/types";

function tag(name: string, weight: number, count = 1): TagCloudEntry {
  return { tag: name, count, weight, recordingIds: Array.from({ length: count }, (_, i) => `r${i}`) };
}

describe("TagCloud", () => {
  const tags = [tag("Budget Planning", 2.4, 3), tag("Vendor Selection", 0.5)];

  it("renders one button per tag, alphabetically, sized and coloured by weight", () => {
    render(<TagCloud tags={tags} selected={null} onSelect={() => {}} />);

    const buttons = screen.getAllByRole("button");
    expect(buttons.map((b) => b.textContent)).toEqual(["Budget Planning", "Vendor Selection"]);
    // The heavier tag renders larger (log-scaled between minPx and maxPx)...
    const heavy = parseFloat(buttons[0].style.fontSize);
    const light = parseFloat(buttons[1].style.fontSize);
    expect(heavy).toBeGreaterThan(light);
    // ...and gets a subtle inline colour that differs by weight (jsdom serialises hsl() to rgb()).
    expect(buttons[0].style.color).not.toBe("");
    expect(buttons[0].style.color).not.toEqual(buttons[1].style.color);
  });

  it("clicking a tag selects it; clicking the selected tag deselects", () => {
    const onSelect = vi.fn();
    const { rerender } = render(<TagCloud tags={tags} selected={null} onSelect={onSelect} />);

    fireEvent.click(screen.getByRole("button", { name: "Budget Planning" }));
    expect(onSelect).toHaveBeenCalledWith("Budget Planning");

    rerender(<TagCloud tags={tags} selected="Budget Planning" onSelect={onSelect} />);
    const selected = screen.getByRole("button", { name: "Budget Planning" });
    expect(selected.getAttribute("aria-pressed")).toBe("true");
    expect(selected.style.color).toBe(""); // selected drops the ramp for the theme's blue class

    fireEvent.click(selected);
    expect(onSelect).toHaveBeenLastCalledWith(null);
  });

  it("shows the recording count in the tooltip", () => {
    render(<TagCloud tags={tags} selected={null} onSelect={() => {}} />);
    expect(screen.getByRole("button", { name: "Budget Planning" }).title).toContain("3");
  });

  it("honours custom pixel bounds (the expanded modal passes larger ones)", () => {
    render(<TagCloud tags={tags} selected={null} onSelect={() => {}} minPx={14} maxPx={40} />);
    expect(screen.getByRole("button", { name: "Budget Planning" }).style.fontSize).toBe("40px");
    expect(screen.getByRole("button", { name: "Vendor Selection" }).style.fontSize).toBe("14px");
  });

  it("renders nothing when there are no tags", () => {
    const { container } = render(<TagCloud tags={[]} selected={null} onSelect={() => {}} />);
    expect(container.firstChild).toBeNull();
  });
});
