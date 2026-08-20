import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";

import ParsedResult from "./ParsedResult";

// The exact shapes the API returns, camelCased by the response serializer from ExtractedTag,
// ExtractedAction and SummaryResult. `LlmModelsControllerTests` pins that wire format on the other side;
// these must stay in step with it, which is why they are written as the server emits them rather than as
// whatever is convenient here.
describe("ParsedResult", () => {
  it("shows tags with their weights", () => {
    render(<ParsedResult parsedKind="Tags" parsed={[{ tag: "Forecast", weight: 0.82 }]} />);

    expect(screen.getByText("Forecast")).toBeDefined();
    expect(screen.getByText("0.82")).toBeDefined();
  });

  it("shows actions as a table with owner and deadline", () => {
    render(
      <ParsedResult
        parsedKind="Actions"
        parsed={[{ text: "Send the deck", actor: "Sam", deadline: "2026-09-01" }]}
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
        parsed={{ summary: "The team agreed to revise the forecast.", name: "Quarterly planning" }}
      />,
    );

    expect(screen.getByText(/revise the forecast/)).toBeDefined();
    expect(screen.getByText("Quarterly planning")).toBeDefined();
  });

  it("shows a summary that has no suggested name", () => {
    // The pipeline asks for a title only when the recording has none, so a named recording produces this.
    render(
      <ParsedResult
        parsedKind="Summary"
        parsed={{ summary: "The team agreed to revise the forecast.", name: null }}
      />,
    );

    expect(screen.getByText(/revise the forecast/)).toBeDefined();
    expect(screen.queryByText(/suggested name/i)).toBeNull();
  });

  it("says the pipeline would have got nothing from an empty extraction", () => {
    // This is the single most valuable state in the whole panel: a call that succeeded, cost tokens, and
    // would have produced no tags at all. Rendering an empty box instead would read as a pass.
    render(<ParsedResult parsedKind="Tags" parsed={[]} />);

    expect(screen.getByText(/extracted no tags/i)).toBeDefined();
  });

  it("says the same for an empty action extraction", () => {
    render(<ParsedResult parsedKind="Actions" parsed={[]} />);

    expect(screen.getByText(/extracted no action items/i)).toBeDefined();
  });

  it("says the same for a summary the parser could not find", () => {
    render(<ParsedResult parsedKind="Summary" parsed={{ summary: "", name: null }} />);

    expect(screen.getByText(/extracted no summary/i)).toBeDefined();
  });
});
