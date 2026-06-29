import { describe, it, expect } from "vitest";
import { dayKey, isoToDayKey, buildMonthGrid, recordingDayKeys, recordingsForDay } from "./calendar";
import type { RecordingSummary } from "./types";

const rec = (id: string, createdAt: string): RecordingSummary => ({
  id, title: id, name: null, source: "Microphone", durationMs: 0, status: "Transcribed",
  createdAt, sectionId: null, sectionName: null, hasActions: false, hasAudio: true,
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
