import { describe, it, expect } from "vitest";
import {
  dayKey, isoToDayKey, buildMonthGrid, recordingDayKeys, recordingsForDay,
  eventDayKeys, visibleGridRange, dayItems, recordingSpan, dayItemSpan, dayEventCount,
} from "./calendar";
import type { CalendarEvent, RecordingSummary } from "./types";

const rec = (
  id: string, createdAt: string, calendarEventId: string | null = null, startedAt: string | null = null,
): RecordingSummary => ({
  id, title: id, name: null, source: "Microphone", durationMs: 0, status: "Transcribed",
  createdAt, sectionId: null, sectionName: null, hasActions: false, hasAudio: true, calendarEventId,
  startedAt,
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

  it("dedupes a linked event: the recording row represents it, the standalone event is dropped", () => {
    // r-linked is linked to event e9 → only the recording appears (it carries both icons), not e9 again.
    const recordings = [rec("r-linked", new Date(2026, 6, 2, 10, 0).toISOString(), "e9")];
    const events = [
      ev("e9", new Date(2026, 6, 2, 9, 0), new Date(2026, 6, 2, 9, 30)), // linked → suppressed
      ev("e-other", new Date(2026, 6, 2, 14, 0), new Date(2026, 6, 2, 15, 0)), // unlinked → kept
    ];
    const items = dayItems(recordings, events, "2026-07-02");
    expect(items.map((i) => (i.type === "event" ? `ev:${i.event.id}` : `rec:${i.recording.id}`)))
      .toEqual(["rec:r-linked", "ev:e-other"]);
  });

  it("dedupes across days: a linked event on another day is still suppressed by its recording", () => {
    // Manual link where the meeting and recording fall on different days: the event must not show as a
    // standalone row anywhere (the recording carries it, on the recording's day).
    const recordings = [rec("r", new Date(2026, 6, 5, 10, 0).toISOString(), "late")];
    const events = [ev("late", new Date(2026, 6, 2, 9, 0), new Date(2026, 6, 2, 9, 30))];
    expect(dayItems(recordings, events, "2026-07-02")).toHaveLength(0); // event suppressed on its own day
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

  // A meeting recorded 23:30-00:30 finishes (and therefore uploads) on the following day. Placing it by
  // createdAt files it under the wrong date - and worse, in a different cell from the very calendar event
  // it is linked to, since the event still sits on the day it started.
  it("places a recording on the day it started, not the day the upload landed", () => {
    const lateNight = [
      rec("night", new Date(2026, 5, 10, 0, 30).toISOString(), null, new Date(2026, 5, 9, 23, 30).toISOString()),
    ];
    expect(recordingDayKeys(lateNight)).toEqual(new Set(["2026-06-09"]));
    expect(recordingsForDay(lateNight, "2026-06-09").map((r) => r.id)).toEqual(["night"]);
    expect(recordingsForDay(lateNight, "2026-06-10")).toEqual([]);
  });

  it("falls back to createdAt when the start is unknown", () => {
    const uploaded = [rec("file", new Date(2026, 5, 10, 0, 30).toISOString())];
    expect(recordingDayKeys(uploaded)).toEqual(new Set(["2026-06-10"]));
  });

  it("orders a day by start time, so a late-uploaded early recording still sorts first", () => {
    const mixed = [
      // Started 09:00 but only uploaded at 17:00 (a long take).
      rec("early", new Date(2026, 5, 9, 17, 0).toISOString(), null, new Date(2026, 5, 9, 9, 0).toISOString()),
      rec("later", new Date(2026, 5, 9, 11, 0).toISOString(), null, new Date(2026, 5, 9, 11, 0).toISOString()),
    ];
    expect(recordingsForDay(mixed, "2026-06-09").map((r) => r.id)).toEqual(["early", "later"]);
  });
});

describe("dayItems with a true start", () => {
  it("timelines a recording at the time it started", () => {
    const recordings = [
      rec("r9", new Date(2026, 6, 2, 17, 0).toISOString(), null, new Date(2026, 6, 2, 9, 0).toISOString()),
    ];
    const events = [ev("e10", new Date(2026, 6, 2, 10, 0), new Date(2026, 6, 2, 10, 30))];
    // Sorted by start, the 09:00 recording precedes the 10:00 event despite uploading at 17:00.
    expect(dayItems(recordings, events, "2026-07-02").map((i) => (i.type === "recording" ? i.recording.id : i.event.id)))
      .toEqual(["r9", "e10"]);
  });
});

/// A recording with fields overridden - the positional `rec()` above can't express durationMs/endedAt.
const recWith = (over: Partial<RecordingSummary>): RecordingSummary => ({
  ...rec("r", new Date(2026, 6, 2, 10, 0).toISOString()),
  ...over,
});

describe("recordingSpan", () => {
  it("starts at startedAt when it is known, ignoring the upload time", () => {
    const { start } = recordingSpan(recWith({
      startedAt: new Date(2026, 6, 2, 9, 0).toISOString(),
      createdAt: new Date(2026, 6, 2, 17, 0).toISOString(),
    }));
    expect(start.getHours()).toBe(9);
  });

  it("falls back to createdAt when the start was never tracked", () => {
    const { start } = recordingSpan(recWith({ startedAt: null, createdAt: new Date(2026, 6, 2, 14, 0).toISOString() }));
    expect(start.getHours()).toBe(14);
  });

  // durationMs is recorded-audio length and excludes paused stretches, so a meeting paused for ten minutes
  // would occupy the wrong slot if the end came from it. endedAt is the wall clock the capture stopped.
  it("ends at endedAt even when durationMs disagrees", () => {
    const { start, end } = recordingSpan(recWith({
      startedAt: new Date(2026, 6, 2, 9, 0).toISOString(),
      endedAt: new Date(2026, 6, 2, 9, 30).toISOString(),
      durationMs: 20 * 60_000,
    }));
    expect(end.getTime() - start.getTime()).toBe(30 * 60_000);
  });

  it("falls back to start + durationMs when the stop is unknown", () => {
    const { start, end } = recordingSpan(recWith({
      startedAt: new Date(2026, 6, 2, 9, 0).toISOString(), endedAt: null, durationMs: 45 * 60_000,
    }));
    expect(end.getTime() - start.getTime()).toBe(45 * 60_000);
  });

  // Clock skew on a machine that pushed both timestamps would otherwise give the block a negative height.
  it("never ends before it starts", () => {
    const { start, end } = recordingSpan(recWith({
      startedAt: new Date(2026, 6, 2, 9, 0).toISOString(),
      endedAt: new Date(2026, 6, 2, 8, 0).toISOString(),
      durationMs: 0,
    }));
    expect(end.getTime()).toBe(start.getTime());
  });

  it("gives a zero-length span to a recording with no duration yet", () => {
    const { start, end } = recordingSpan(recWith({ startedAt: null, endedAt: null, durationMs: 0 }));
    expect(end.getTime()).toBe(start.getTime());
  });
});

describe("dayItemSpan", () => {
  const spanOf = (item: CalendarEvent | RecordingSummary, key: string) =>
    dayItemSpan("summary" in item
      ? { type: "event", time: 0, event: item as CalendarEvent }
      : { type: "recording", time: 0, recording: item as RecordingSummary }, key);

  it("reduces a timed event to fractional local hours", () => {
    const span = spanOf(ev("e", new Date(2026, 6, 2, 9, 0), new Date(2026, 6, 2, 9, 30)), "2026-07-02");
    expect(span).toEqual({ startHour: 9, endHour: 9.5, allDay: false });
  });

  it("treats a meeting that began on an earlier day as all-day on this one", () => {
    const span = spanOf(ev("span", new Date(2026, 6, 1, 22, 0), new Date(2026, 6, 3, 1, 0)), "2026-07-02");
    expect(span.allDay).toBe(true);
  });

  it("honours the server's allDay flag", () => {
    const flagged: CalendarEvent = {
      ...ev("holiday", new Date(2026, 6, 2, 0, 0), new Date(2026, 6, 3, 0, 0)), allDay: true,
    };
    expect(spanOf(flagged, "2026-07-02").allDay).toBe(true);
  });

  it("treats a local-midnight-to-midnight span as all-day even without the flag", () => {
    expect(spanOf(ev("d", new Date(2026, 6, 2), new Date(2026, 6, 3)), "2026-07-02").allDay).toBe(true);
  });

  // A slim payload can carry start === end. That is a zero-length timed event sitting at its own hour,
  // not an all-day one - misclassifying it would banish it to the chip row.
  it("does not mistake a zero-length event for an all-day one", () => {
    const midnight = spanOf(ev("z", new Date(2026, 6, 2), new Date(2026, 6, 2)), "2026-07-02");
    expect(midnight.allDay).toBe(false);
    expect(midnight.startHour).toBe(0);
  });

  it("lets a recording run past midnight rather than clamping it here", () => {
    const night = recWith({
      startedAt: new Date(2026, 6, 2, 23, 30).toISOString(),
      endedAt: new Date(2026, 6, 3, 0, 30).toISOString(),
    });
    const span = spanOf(night, "2026-07-02");
    expect(span.startHour).toBe(23.5);
    expect(span.endHour).toBe(24.5);
  });

  it("never marks a recording all-day", () => {
    expect(spanOf(recWith({ startedAt: new Date(2026, 6, 2).toISOString(), durationMs: 0 }), "2026-07-02").allDay)
      .toBe(false);
  });
});

describe("dayEventCount", () => {
  const ev = (id: string, start: string, end: string) =>
    ({ id, summary: id, start, end, htmlLink: null }) as CalendarEvent;

  it("counts an event on the day", () => {
    const events = [ev("a", "2026-08-10T09:00:00Z", "2026-08-10T10:00:00Z")];
    expect(dayEventCount(events, "2026-08-10")).toBe(1);
  });

  it("counts an event on every day it spans, matching how the grid draws it", () => {
    const events = [ev("overnight", "2026-08-10T22:00:00Z", "2026-08-11T01:00:00Z")];
    expect(dayEventCount(events, "2026-08-10")).toBe(1);
    expect(dayEventCount(events, "2026-08-11")).toBe(1);
  });

  it("counts nothing on an unrelated day", () => {
    const events = [ev("a", "2026-08-10T09:00:00Z", "2026-08-10T10:00:00Z")];
    expect(dayEventCount(events, "2026-08-12")).toBe(0);
  });
});
