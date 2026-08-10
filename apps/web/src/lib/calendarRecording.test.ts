import { describe, it, expect } from "vitest";
import {
  resolveCalendarStopAt,
  earlierStop,
  nextSilenceState,
  shouldStopForSilence,
  shouldPromptExtend,
  extendedStopAt,
  idleSilence,
  type CalendarRecordingSettings,
} from "./calendarRecording";

const MIN = 60_000;
const settings = (over: Partial<CalendarRecordingSettings> = {}): CalendarRecordingSettings => ({
  enabled: true,
  afterMinutes: 3,
  silenceSeconds: 30,
  ...over,
});

describe("resolveCalendarStopAt", () => {
  const start = Date.parse("2026-08-09T10:00:00Z");
  const endsAt = "2026-08-09T11:00:00Z";

  it("stops the configured number of minutes after the invite ends", () => {
    expect(resolveCalendarStopAt(endsAt, settings(), start)).toBe(Date.parse(endsAt) + 3 * MIN);
  });

  it("returns null when auto-stop is off", () => {
    expect(resolveCalendarStopAt(endsAt, settings({ enabled: false }), start)).toBeNull();
  });

  it("returns null without an end time (an all-day or malformed event)", () => {
    expect(resolveCalendarStopAt(null, settings(), start)).toBeNull();
    expect(resolveCalendarStopAt("not a date", settings(), start)).toBeNull();
  });

  it("never schedules a stop in the past when the meeting has already ended", () => {
    // Joining late, after the invite's end time: the naive answer is behind us, which would stop the
    // recording on its first tick. Fall back to the same wait measured from now.
    const lateStart = Date.parse(endsAt) + 30 * MIN;
    expect(resolveCalendarStopAt(endsAt, settings(), lateStart)).toBe(lateStart + 3 * MIN);
  });

  it("treats a non-positive wait as the default rather than stopping instantly", () => {
    expect(resolveCalendarStopAt(endsAt, settings({ afterMinutes: 0 }), start)).toBe(Date.parse(endsAt) + 3 * MIN);
    expect(resolveCalendarStopAt(endsAt, settings({ afterMinutes: -9 }), start)).toBe(Date.parse(endsAt) + 3 * MIN);
  });
});

describe("earlierStop", () => {
  it("takes whichever target comes first", () => {
    expect(earlierStop(500, 100)).toBe(100);
    expect(earlierStop(100, 500)).toBe(100);
  });

  it("falls through nulls", () => {
    expect(earlierStop(null, 100)).toBe(100);
    expect(earlierStop(100, null)).toBe(100);
    expect(earlierStop(null, null)).toBeNull();
  });
});

describe("silence tracking", () => {
  const LOUD = 0.5;
  const QUIET = 0;

  it("ignores silence before anything has been heard", () => {
    // A recording started while the room is still quiet must not be killed at the outset - that is the
    // whole reason this is a state machine rather than a running total.
    let s = idleSilence();
    for (let i = 0; i < 100; i++) s = nextSilenceState(s, QUIET, 1000);
    expect(s.silentMs).toBe(0);
    expect(shouldStopForSilence(s, 30_000)).toBe(false);
  });

  it("accumulates silence once sound has been heard", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    expect(s.heardSound).toBe(true);
    s = nextSilenceState(s, QUIET, 10_000);
    s = nextSilenceState(s, QUIET, 10_000);
    expect(s.silentMs).toBe(20_000);
    expect(shouldStopForSilence(s, 30_000)).toBe(false);

    s = nextSilenceState(s, QUIET, 10_000);
    expect(shouldStopForSilence(s, 30_000)).toBe(true);
  });

  it("resets the run the moment sound returns", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 25_000);
    s = nextSilenceState(s, LOUD, 100); // someone speaks again
    expect(s.silentMs).toBe(0);
    expect(shouldStopForSilence(s, 30_000)).toBe(false);
  });

  it("keeps 'heard sound' across a silent run, so a later pause still counts", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 100);
    s = nextSilenceState(s, QUIET, 40_000);
    expect(s.heardSound).toBe(true);
  });

  it("never stops on a non-positive threshold", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 100);
    s = nextSilenceState(s, QUIET, 60_000);
    expect(shouldStopForSilence(s, 0)).toBe(false);
    expect(shouldStopForSilence(s, -1)).toBe(false);
  });
});

describe("shouldPromptExtend", () => {
  const LOUD = 0.5;
  const QUIET = 0;

  it("asks when someone has spoken recently", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 2_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(true);
  });

  it("does not ask once the room has been quiet for a while - there is nobody to answer", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 30_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });

  it("does not ask when nothing has ever been heard (joined early, meeting never happened)", () => {
    let s = idleSilence();
    for (let i = 0; i < 10; i++) s = nextSilenceState(s, QUIET, 1000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });

  it("treats the threshold as exclusive, matching shouldStopForSilence's inclusive one", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 10_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });
});

describe("extendedStopAt", () => {
  const now = Date.parse("2026-08-10T09:00:00Z");

  it("adds the user's own overrun allowance", () => {
    expect(extendedStopAt(now, 5)).toBe(now + 5 * MIN);
  });

  it("falls back to the default rather than re-asking instantly", () => {
    expect(extendedStopAt(now, 0)).toBe(now + 3 * MIN);
    expect(extendedStopAt(now, -9)).toBe(now + 3 * MIN);
  });
});
