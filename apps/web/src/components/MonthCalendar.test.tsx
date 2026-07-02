import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import MonthCalendar from "./MonthCalendar";
import { dayKey } from "../lib/calendar";

// June 2026: the 15th has recordings, the 20th only has calendar events, the 15th also has an event.
const YEAR = 2026;
const MONTH = 5; // 0-based → June
const withRec = dayKey(new Date(YEAR, MONTH, 15));
const withEventOnly = dayKey(new Date(YEAR, MONTH, 20));

function renderCal(selectedKey: string | null, events?: Set<string>) {
  render(
    <MonthCalendar
      year={YEAR}
      month={MONTH}
      daysWithRecordings={new Set([withRec])}
      daysWithEvents={events}
      selectedKey={selectedKey}
      onSelect={vi.fn()}
      onPrev={vi.fn()}
      onNext={vi.fn()}
    />,
  );
}

describe("MonthCalendar", () => {
  it("keeps a constant inset ring on day cells so selection can't reflow the grid", () => {
    renderCal(null);
    const cell = screen.getByRole("button", { name: "15" });
    expect(cell.className).toContain("ring-2");
    expect(cell.className).toContain("ring-inset");
    expect(cell.className).toContain("ring-transparent"); // not selected, not today
  });

  it("colours the reserved ring green when a day is selected (no extra width)", () => {
    renderCal(withRec);
    const cell = screen.getByRole("button", { name: "15" });
    expect(cell.className).toContain("ring-2");
    expect(cell.className).toContain("ring-green-500");
    expect(cell.className).not.toContain("ring-transparent");
  });

  it("makes every in-month day clickable (empty days are grey, not disabled)", () => {
    renderCal(null);
    const empty = screen.getByRole("button", { name: "10" }) as HTMLButtonElement;
    expect(empty.disabled).toBe(false); // all days clickable now (for inspecting events / future scheduling)
    expect(empty.className).toContain("text-gray-400");
  });

  it("shades an events-only day a darker green than an empty day", () => {
    renderCal(null, new Set([withEventOnly]));
    const eventDay = screen.getByRole("button", { name: "20" });
    expect(eventDay.className).toContain("bg-green-300/60"); // events-only fill
  });

  it("marks a recording day that also has events with a dot", () => {
    const { container } = render(
      <MonthCalendar
        year={YEAR}
        month={MONTH}
        daysWithRecordings={new Set([withRec])}
        daysWithEvents={new Set([withRec])}
        selectedKey={null}
        onSelect={vi.fn()}
        onPrev={vi.fn()}
        onNext={vi.fn()}
      />,
    );
    const recDay = screen.getByRole("button", { name: "15" });
    expect(recDay.className).toContain("bg-green-100"); // still a recording (green) cell
    expect(recDay.querySelector("span[aria-hidden]")).not.toBeNull(); // + the events dot
    expect(container).toBeTruthy();
  });
});
