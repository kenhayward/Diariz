import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import CollapsibleSection from "./CollapsibleSection";

describe("CollapsibleSection", () => {
  it("renders the title and children, collapsing/expanding on header click", () => {
    render(
      <CollapsibleSection title="Summary">
        <p>Body text</p>
      </CollapsibleSection>,
    );
    expect(screen.getByRole("heading", { name: "Summary" })).toBeTruthy();
    expect(screen.getByText("Body text")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /collapse summary section/i }));
    expect(screen.queryByText("Body text")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /expand summary section/i }));
    expect(screen.getByText("Body text")).toBeTruthy();
  });

  it("collapses when clicking the title text — the whole header strip is the hit area", () => {
    render(
      <CollapsibleSection title="Speakers">
        <p>Body</p>
      </CollapsibleSection>,
    );
    fireEvent.click(screen.getByText("Speakers")); // title sits inside the header button
    expect(screen.queryByText("Body")).toBeNull();
  });

  it("honours defaultCollapsed", () => {
    render(
      <CollapsibleSection title="Actions" defaultCollapsed>
        <p>Hidden</p>
      </CollapsibleSection>,
    );
    expect(screen.queryByText("Hidden")).toBeNull();
    expect(screen.getByRole("button", { name: /expand actions section/i })).toBeTruthy();
  });
});
