import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import MonthCalendar from "./MonthCalendar";
import { dayKey } from "../lib/calendar";

// June 2026: pick the 15th as the "has recordings" / selectable day.
const YEAR = 2026;
const MONTH = 5; // 0-based → June
const withRec = dayKey(new Date(YEAR, MONTH, 15));

function renderCal(selectedKey: string | null) {
  render(
    <MonthCalendar
      year={YEAR}
      month={MONTH}
      daysWithRecordings={new Set([withRec])}
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
    // The reserved ring (same width in every state) means only the colour changes on select.
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

  it("uses a darker grey for unavailable (no-recording) day numbers", () => {
    renderCal(null);
    const empty = screen.getByRole("button", { name: "10" }) as HTMLButtonElement; // a day without recordings
    expect(empty.disabled).toBe(true);
    expect(empty.className).toContain("text-gray-400"); // darkened from text-gray-300
  });
});
