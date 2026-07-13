import { describe, it, expect } from "vitest";
import { parseTimeToday, resolveStopAt, shouldStop, RELATIVE_MINUTES } from "./recorderSchedule";

// A fixed "now": 2026-07-13 14:00:00 local.
const now = new Date(2026, 6, 13, 14, 0, 0).getTime();

describe("parseTimeToday", () => {
  it("parses HH:MM into today's epoch (local)", () => {
    expect(parseTimeToday("15:30", now)).toBe(new Date(2026, 6, 13, 15, 30, 0).getTime());
  });
  it("returns null for blank or malformed input", () => {
    expect(parseTimeToday("", now)).toBeNull();
    expect(parseTimeToday("25:99", now)).toBeNull();
    expect(parseTimeToday("nope", now)).toBeNull();
  });
});

describe("resolveStopAt", () => {
  it("off -> null", () => {
    expect(resolveStopAt("off", "", now, now)).toBeNull();
  });
  it("relative -> anchor + N minutes", () => {
    expect(resolveStopAt("in15", "", now, now)).toBe(now + 15 * 60_000);
    expect(resolveStopAt("in30", "", now, now)).toBe(now + 30 * 60_000);
    expect(resolveStopAt("in60", "", now, now)).toBe(now + 60 * 60_000);
    // Uses the passed anchor, not `now`, so a pre-recording choice anchors to record-start.
    expect(resolveStopAt("in15", "", now + 5 * 60_000, now)).toBe(now + 20 * 60_000);
  });
  it("at -> today's HH:MM only when in the future", () => {
    expect(resolveStopAt("at", "15:00", now, now)).toBe(new Date(2026, 6, 13, 15, 0, 0).getTime());
    expect(resolveStopAt("at", "13:00", now, now)).toBeNull(); // past
    expect(resolveStopAt("at", "", now, now)).toBeNull(); // blank
  });
});

describe("shouldStop", () => {
  it("true only once now has reached a non-null target", () => {
    expect(shouldStop(now + 1000, now)).toBe(false);
    expect(shouldStop(now, now)).toBe(true);
    expect(shouldStop(now - 1, now)).toBe(true);
    expect(shouldStop(null, now)).toBe(false);
  });
});

describe("RELATIVE_MINUTES", () => {
  it("maps the relative choices to minutes", () => {
    expect(RELATIVE_MINUTES).toEqual({ in15: 15, in30: 30, in60: 60 });
  });
});
