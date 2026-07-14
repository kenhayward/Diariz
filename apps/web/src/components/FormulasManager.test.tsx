import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import FormulasManager from "./FormulasManager";
import type { FormulaResult } from "../lib/types";

const result = (over: Partial<FormulaResult> = {}): FormulaResult => ({
  id: "r1",
  recordingId: "rec-1",
  name: "Action Items",
  status: "Ready",
  error: null,
  createdByUserId: "u1",
  createdAt: new Date(Date.now() - 5 * 60_000).toISOString(),
  updatedAt: new Date().toISOString(),
  origin: { kind: "personal", personName: "Ada Lovelace", personPictureUrl: null },
  ...over,
});

describe("FormulasManager", () => {
  it("shows an empty state when there are no results", () => {
    render(<FormulasManager results={[]} selectedId={null} onSelect={vi.fn()} />);
    expect(screen.getByText(/nothing generated yet/i)).toBeTruthy();
  });

  it("renders each result's name and a generated-from meta line", () => {
    render(<FormulasManager results={[result()]} selectedId={null} onSelect={vi.fn()} />);
    expect(screen.getByText("Action Items")).toBeTruthy();
    expect(screen.getByText(/generated .* from the "Action Items" formula/i)).toBeTruthy();
  });

  it("selects a result on click and deselects on a second click", () => {
    const onSelect = vi.fn();
    render(<FormulasManager results={[result()]} selectedId={null} onSelect={onSelect} />);
    fireEvent.click(screen.getByText("Action Items"));
    expect(onSelect).toHaveBeenCalledWith("r1");
  });

  it("clicking the already-selected result clears the selection", () => {
    const onSelect = vi.fn();
    render(<FormulasManager results={[result()]} selectedId="r1" onSelect={onSelect} />);
    fireEvent.click(screen.getByText("Action Items"));
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  it("only one result is marked selected at a time (single-select)", () => {
    const two = [result({ id: "r1", name: "One" }), result({ id: "r2", name: "Two" })];
    render(<FormulasManager results={two} selectedId="r1" onSelect={vi.fn()} />);
    const buttons = screen.getAllByRole("button");
    expect(buttons.find((b) => b.textContent?.includes("One"))?.getAttribute("aria-pressed")).toBe("true");
    expect(buttons.find((b) => b.textContent?.includes("Two"))?.getAttribute("aria-pressed")).toBe("false");
  });

  it("renders a Generating result as a non-openable spinner row", () => {
    const onSelect = vi.fn();
    const { container } = render(
      <FormulasManager
        results={[result({ id: "g1", name: "Action Items", status: "Generating" })]}
        selectedId={null}
        onSelect={onSelect}
      />,
    );
    // A "generating" affordance is shown (the localised label) plus a spinner element.
    expect(screen.getByText(/generating/i)).toBeTruthy();
    expect(container.querySelector(".animate-spin")).toBeTruthy();
    // It is not a selectable button, and clicking it does not select it for viewing.
    expect(screen.queryByRole("button")).toBeNull();
    fireEvent.click(screen.getByText(/generating/i));
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("renders a Failed result with an error marker exposing the error", () => {
    render(
      <FormulasManager
        results={[result({ id: "x1", name: "Action Items", status: "Failed", error: "Model timed out" })]}
        selectedId={null}
        onSelect={vi.fn()}
      />,
    );
    expect(screen.getByText(/model timed out/i)).toBeTruthy();
  });

  it("shows the Diariz logo for a built-in result and initials for a personal one", () => {
    const two = [
      result({ id: "r1", name: "Recap", origin: { kind: "diariz", personName: null, personPictureUrl: null } }),
      result({ id: "r2", name: "Mine", origin: { kind: "personal", personName: "Ada Lovelace", personPictureUrl: null } }),
    ];
    const { container } = render(<FormulasManager results={two} selectedId={null} onSelect={vi.fn()} />);
    // Diariz -> the logo image (decorative alt="" -> query by src rather than role)
    expect(container.querySelector('img[src="/logo.png"]')).toBeTruthy();
    // Personal (no picture) -> initials bubble "AL"
    expect(screen.getByText("AL")).toBeTruthy();
  });

  // An API older than 0.130.2 doesn't send `origin` at all. Dereferencing it threw, and because the
  // throw happened during render it took out the whole recording-detail panel behind the ErrorBoundary
  // ("Something went wrong showing this page") - a hard crash over one missing decorative field.
  it("still renders a result whose origin is missing, rather than crashing the page", () => {
    const noOrigin = [result({ id: "r1", name: "Recap", origin: undefined as never })];
    render(<FormulasManager results={noOrigin} selectedId={null} onSelect={vi.fn()} />);
    expect(screen.getByText("Recap")).toBeTruthy();
    // Provenance is unknown, so it shows the unknown-person placeholder rather than claiming an origin.
    expect(screen.getByText("?")).toBeTruthy();
  });

  it("still renders a failed result whose origin is missing", () => {
    const noOrigin = [
      result({ id: "r1", name: "Recap", status: "Failed", error: "Model timed out", origin: undefined as never }),
    ];
    render(<FormulasManager results={noOrigin} selectedId={null} onSelect={vi.fn()} />);
    expect(screen.getByText(/model timed out/i)).toBeTruthy();
  });

  it("keeps a missing origin from claiming the result is official", () => {
    const noOrigin = [result({ id: "r1", name: "Recap", origin: undefined as never })];
    const { container } = render(<FormulasManager results={noOrigin} selectedId={null} onSelect={vi.fn()} />);
    expect(container.querySelector('img[src="/logo.png"]')).toBeNull();
  });
});
