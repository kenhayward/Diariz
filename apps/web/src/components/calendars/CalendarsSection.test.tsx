import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

vi.mock("./GoogleCalendarCard", () => ({ default: () => <div>GOOGLE_CARD</div> }));
vi.mock("./OutlookCard", () => ({ default: () => <div>OUTLOOK_CARD</div> }));
vi.mock("./FeedsCard", () => ({ default: () => <div>FEEDS_CARD</div> }));

import CalendarsSection from "./CalendarsSection";

describe("CalendarsSection", () => {
  it("heads the panel and stacks every calendar source", () => {
    render(<CalendarsSection />);

    expect(screen.getByText(/everything that feeds the calendar tab/i)).toBeTruthy();
    for (const card of ["GOOGLE_CARD", "OUTLOOK_CARD", "FEEDS_CARD"]) {
      expect(screen.getByText(card)).toBeTruthy();
    }
  });

  // Each card owns its own queries, so one provider failing degrades to one broken card rather than an
  // empty panel. The stack itself holds no state, which is what keeps a fourth source cheap.
  it("keeps the sources in a fixed order, connected accounts first", () => {
    const { container } = render(<CalendarsSection />);
    const order = [...container.querySelectorAll("div")]
      .map((d) => d.textContent)
      .filter((x) => x === "GOOGLE_CARD" || x === "OUTLOOK_CARD" || x === "FEEDS_CARD");
    expect(order).toEqual(["GOOGLE_CARD", "OUTLOOK_CARD", "FEEDS_CARD"]);
  });
});
