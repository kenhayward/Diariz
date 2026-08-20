import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";

import ParsedResult from "./ParsedResult";

describe("ParsedResult", () => {
  it("shows tags with their weights", () => {
    render(<ParsedResult parsedKind="Tags" parsedJson='[{"tag":"Forecast","weight":0.82}]' />);

    expect(screen.getByText("Forecast")).toBeDefined();
    expect(screen.getByText("0.82")).toBeDefined();
  });

  it("shows actions as a table with owner and deadline", () => {
    render(
      <ParsedResult
        parsedKind="Actions"
        parsedJson='[{"text":"Send the deck","actor":"Sam","deadline":"2026-09-01"}]'
      />,
    );

    expect(screen.getByText("Send the deck")).toBeDefined();
    expect(screen.getByText("Sam")).toBeDefined();
    expect(screen.getByText("2026-09-01")).toBeDefined();
  });

  it("shows the suggested name alongside a summary", () => {
    render(
      <ParsedResult
        parsedKind="Summary"
        parsedJson='{"summary":"The team agreed to revise the forecast.","name":"Quarterly planning"}'
      />,
    );

    expect(screen.getByText(/revise the forecast/)).toBeDefined();
    expect(screen.getByText("Quarterly planning")).toBeDefined();
  });

  it("says the pipeline would have got nothing from an empty extraction", () => {
    // This is the single most valuable state in the whole panel: a call that succeeded, cost tokens, and
    // would have produced no tags at all. Rendering an empty box instead would read as a pass.
    render(<ParsedResult parsedKind="Tags" parsedJson="[]" />);

    expect(screen.getByText(/extracted no tags/i)).toBeDefined();
  });

  it("says the same for an empty action extraction", () => {
    render(<ParsedResult parsedKind="Actions" parsedJson="[]" />);

    expect(screen.getByText(/extracted no action items/i)).toBeDefined();
  });

  it("survives a payload it cannot read", () => {
    // Never crash the drawer over a display concern: the raw reply is still shown above this.
    render(<ParsedResult parsedKind="Tags" parsedJson="not json" />);

    expect(screen.getByText(/extracted no tags/i)).toBeDefined();
  });
});
