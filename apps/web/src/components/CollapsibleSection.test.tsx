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

  it("does not toggle when clicking the title text — only the chevron collapses", () => {
    // The title is a plain heading (not a button) so it never collides with same-named controls
    // elsewhere (e.g. the 'Actions' kebab). The collapse control is the chevron alone.
    render(
      <CollapsibleSection title="Speakers">
        <p>Body</p>
      </CollapsibleSection>,
    );
    fireEvent.click(screen.getByText("Speakers"));
    expect(screen.getByText("Body")).toBeTruthy(); // still expanded — the heading is inert

    fireEvent.click(screen.getByRole("button", { name: /collapse speakers section/i }));
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
