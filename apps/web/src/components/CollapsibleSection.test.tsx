import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
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

  it("toggles when clicking the title text — the header (outside the toolbar) is the hit area", () => {
    // The title is a heading, not a button, so clicking it never collides with same-named controls
    // elsewhere (e.g. the 'Actions' kebab), but it still toggles the section.
    render(
      <CollapsibleSection title="Speakers">
        <p>Body</p>
      </CollapsibleSection>,
    );
    fireEvent.click(screen.getByText("Speakers"));
    expect(screen.queryByText("Body")).toBeNull(); // collapsed

    fireEvent.click(screen.getByText("Speakers"));
    expect(screen.getByText("Body")).toBeTruthy(); // expanded again
  });

  it("does not toggle when clicking a header-action button (toolbar is outside the hit area)", () => {
    const onAct = vi.fn();
    render(
      <CollapsibleSection title="Summary" headerActions={<button onClick={onAct}>Act</button>}>
        <p>Body</p>
      </CollapsibleSection>,
    );
    fireEvent.click(screen.getByRole("button", { name: "Act" }));
    expect(onAct).toHaveBeenCalledOnce();
    expect(screen.getByText("Body")).toBeTruthy(); // still expanded — the toolbar doesn't toggle
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
