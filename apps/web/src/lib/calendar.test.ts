import { describe, it, expect } from "vitest";
import {
  dayKey, isoToDayKey, buildMonthGrid, recordingDayKeys, recordingsForDay,
  eventDayKeys, visibleGridRange, dayItems,
} from "./calendar";
import type { CalendarEvent, RecordingSummary } from "./types";

const rec = (id: string, createdAt: string): RecordingSummary => ({
  id, title: id, name: null, source: "Microphone", durationMs: 0, status: "Transcribed",
  createdAt, sectionId: null, sectionName: null, hasActions: false, hasAudio: true,
});

/// Build an event from local Date parts so the ISO round-trips back to the same local day regardless of
/// the machine timezone (keeps these tests deterministic in CI).
const ev = (id: string, start: Date, end: Date): CalendarEvent => ({
  id, summary: id, start: start.toISOString(), end: end.toISOString(), htmlLink: null,
});

describe("eventDayKeys", () => {
  it("maps a single-day timed event to its day", () => {
    const keys = eventDayKeys([ev("a", new Date(2026, 6, 2, 9, 0), new Date(2026, 6, 2, 10, 0))]);
    expect([...keys]).toEqual(["2026-07-02"]);
  });

  it("treats an all-day event's exclusive midnight end as not spilling into the next day", () => {
    // Google all-day: start 07-02 00:00, end 07-03 00:00 (exclusive) → only 07-02.
    const keys = eventDayKeys([ev("a", new Date(2026, 6, 2), new Date(2026, 6, 3))]);
    expect([...keys]).toEqual(["2026-07-02"]);
  });

  it("expands a multi-day event across every day it spans", () => {
    const keys = eventDayKeys([ev("a", new Date(2026, 6, 2, 22, 0), new Date(2026, 6, 4, 2, 0))]);
    expect([...keys].sort()).toEqual(["2026-07-02", "2026-07-03", "2026-07-04"]);
  });
});

describe("visibleGridRange", () => {
  it("spans the first visible cell to the day after the last visible cell", () => {
    const { timeMin, timeMax } = visibleGridRange(2026, 6); // July 2026
    // July 1 2026 is a Wednesday; Monday-first grid starts Mon June 29.
    expect(isoToDayKey(timeMin)).toBe("2026-06-29");
    // timeMax is exclusive (one day past the last cell), so it must be strictly after any July day.
    expect(new Date(timeMax).getTime()).toBeGreaterThan(new Date(2026, 6, 31).getTime());
  });
});

describe("dayItems", () => {
  it("merges recordings and events for the day, ordered by time", () => {
    const recordings = [rec("r10", new Date(2026, 6, 2, 10, 0).toISOString())];
    const events = [ev("e9", new Date(2026, 6, 2, 9, 0), new Date(2026, 6, 2, 9, 30))];
    const items = dayItems(recordings, events, "2026-07-02");
    expect(items.map((i) => i.type)).toEqual(["event", "recording"]); // 09:00 event before 10:00 recording
    expect(items[0].type === "event" && items[0].event.id).toBe("e9");
    expect(items[1].type === "recording" && items[1].recording.id).toBe("r10");
  });

  it("includes an event that spans into the day but excludes other days' items", () => {
    const events = [ev("span", new Date(2026, 6, 1, 22, 0), new Date(2026, 6, 3, 1, 0))];
    const recordings = [rec("other", new Date(2026, 6, 5, 10, 0).toISOString())];
    const items = dayItems(recordings, events, "2026-07-02");
    expect(items).toHaveLength(1);
    expect(items[0].type === "event" && items[0].event.id).toBe("span");
  });
});

describe("dayKey", () => {
  it("formats local date components as YYYY-MM-DD", () => {
    expect(dayKey(new Date(2026, 5, 9))).toBe("2026-06-09"); // June 9 (zero-based month)
  });
});

describe("buildMonthGrid", () => {
  it("returns a fixed 6×7 grid that starts on a Monday and covers the whole month", () => {
    const weeks = buildMonthGrid(2026, 5); // June 2026
    expect(weeks).toHaveLength(6);
    expect(weeks.every((w) => w.length === 7)).toBe(true);
    expect(weeks[0][0].date.getDay()).toBe(1); // Monday

    const inMonth = weeks.flat().filter((d) => d.inMonth);
    expect(inMonth).toHaveLength(30);                 // June has 30 days
    expect(inMonth[0].key).toBe("2026-06-01");
    expect(inMonth.at(-1)!.key).toBe("2026-06-30");
    // The first cell is a leading day from May (June 1 2026 is a Monday, so offset 0 → still June 1).
    expect(weeks[0][0].key).toBe("2026-06-01");
  });

  it("pads the first week with trailing days of the previous month when needed", () => {
    const weeks = buildMonthGrid(2026, 6); // July 2026 — July 1 is a Wednesday
    expect(weeks[0][0].inMonth).toBe(false);          // Monday June 29
    expect(weeks[0][0].key).toBe("2026-06-29");
    expect(weeks[0][2].key).toBe("2026-07-01");        // Wednesday
    expect(weeks[0][2].inMonth).toBe(true);
  });
});

describe("recordingDayKeys / recordingsForDay", () => {
  const recs = [
    rec("a", new Date(2026, 5, 9, 9, 0).toISOString()),
    rec("b", new Date(2026, 5, 9, 14, 30).toISOString()),
    rec("c", new Date(2026, 5, 10, 8, 0).toISOString()),
  ];

  it("collects the distinct local day-keys that have recordings", () => {
    expect(recordingDayKeys(recs)).toEqual(new Set(["2026-06-09", "2026-06-10"]));
  });

  it("returns a day's recordings oldest-first", () => {
    expect(recordingsForDay(recs, "2026-06-09").map((r) => r.id)).toEqual(["a", "b"]);
    expect(recordingsForDay(recs, "2026-06-11")).toEqual([]);
  });

  it("files a recording under its local date via isoToDayKey", () => {
    expect(isoToDayKey(new Date(2026, 0, 1, 23, 59).toISOString())).toBe("2026-01-01");
  });
});
