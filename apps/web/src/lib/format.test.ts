import { describe, it, expect } from "vitest";
import {
  formatBytes, storagePercent, bytesToGb, gbToBytes, formatDuration,
  formatLongDate, formatTimeHm, formatDurationHm, formatDurationApprox, formatRelativeTime,
} from "./format";

describe("formatDurationApprox", () => {
  it("reads as whole minutes under an hour", () => {
    expect(formatDurationApprox(21 * 60_000)).toBe("21 min");
    expect(formatDurationApprox(65_000)).toBe("1 min");
  });

  it("splits into hours and minutes past the hour", () => {
    expect(formatDurationApprox(3_900_000)).toBe("1 h 5 min");
    expect(formatDurationApprox(2 * 3_600_000)).toBe("2 h");
  });

  it("rounds a sub-minute recording up to a minute rather than showing '0 min'", () => {
    expect(formatDurationApprox(20_000)).toBe("1 min");
  });

  it("is '0 min' only for a genuinely empty duration", () => {
    expect(formatDurationApprox(0)).toBe("0 min");
    expect(formatDurationApprox(-5)).toBe("0 min");
  });
});

describe("formatDuration", () => {
  it("formats as m:ss under an hour, with no leading-zero minutes", () => {
    expect(formatDuration(419_000)).toBe("6:59");
    expect(formatDuration(819_000)).toBe("13:39");
    expect(formatDuration(45_000)).toBe("0:45");
    expect(formatDuration(0)).toBe("0:00");
  });

  it("includes hours (no leading zero) once past an hour", () => {
    expect(formatDuration(3_661_000)).toBe("1:01:01");
    expect(formatDuration(3_600_000)).toBe("1:00:00");
    expect(formatDuration(36_000_000)).toBe("10:00:00");
  });

  it("rounds to the nearest second and clamps negatives to 0:00", () => {
    expect(formatDuration(1_500)).toBe("0:02"); // 1.5s → 2s
    expect(formatDuration(-5)).toBe("0:00");
  });
});

describe("formatBytes", () => {
  it("formats zero / negatives as 0 B", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(-5)).toBe("0 B");
  });

  it("scales through binary units", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1024)).toBe("1 KB");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(5 * 1024 ** 3)).toBe("5 GB");
    expect(formatBytes(1.2 * 1024 ** 3)).toBe("1.2 GB");
  });
});

describe("storagePercent", () => {
  it("computes the rounded percentage", () => {
    expect(storagePercent(1024 ** 3, 5 * 1024 ** 3)).toBe(20);
    expect(storagePercent(0, 5 * 1024 ** 3)).toBe(0);
  });
  it("is 0 when there is no quota", () => {
    expect(storagePercent(100, 0)).toBe(0);
  });
});

describe("formatLongDate", () => {
  it("uses an ordinal day suffix for English locales", () => {
    // Construct in local time so the day component is timezone-independent.
    const d = new Date(2026, 2, 23).toISOString(); // 23 March 2026
    expect(formatLongDate(d, "en")).toBe("23rd March 2026");
    expect(formatLongDate(new Date(2026, 2, 1).toISOString(), "en-GB")).toBe("1st March 2026");
    expect(formatLongDate(new Date(2026, 2, 2).toISOString(), "en")).toBe("2nd March 2026");
    expect(formatLongDate(new Date(2026, 2, 11).toISOString(), "en")).toBe("11th March 2026");
  });

  it("defaults to English when no locale is given", () => {
    expect(formatLongDate(new Date(2026, 2, 23).toISOString())).toBe("23rd March 2026");
  });

  it("uses the locale's natural long form for non-English locales (no English ordinal)", () => {
    const out = formatLongDate(new Date(2026, 2, 23).toISOString(), "de-DE");
    expect(out).toContain("2026");
    expect(out).not.toContain("rd"); // no English ordinal suffix
  });
});

describe("formatTimeHm", () => {
  it("formats as fixed 24-hour hh:mm", () => {
    expect(formatTimeHm(new Date(2026, 2, 23, 9, 5).toISOString())).toBe("09:05");
    expect(formatTimeHm(new Date(2026, 2, 23, 14, 30).toISOString())).toBe("14:30");
    expect(formatTimeHm(new Date(2026, 2, 23, 0, 0).toISOString())).toBe("00:00");
  });
});

describe("formatDurationHm", () => {
  it("formats as zero-padded hours:minutes", () => {
    expect(formatDurationHm(65_000)).toBe("00:01");
    expect(formatDurationHm(3_900_000)).toBe("01:05");
    expect(formatDurationHm(0)).toBe("00:00");
    expect(formatDurationHm(-5)).toBe("00:00");
  });
});

describe("GB conversion", () => {
  it("round-trips whole gigabytes", () => {
    expect(gbToBytes(5)).toBe(5 * 1024 ** 3);
    expect(bytesToGb(5 * 1024 ** 3)).toBe(5);
  });
});

describe("formatRelativeTime", () => {
  const now = new Date(2026, 2, 23, 12, 0, 0);

  it("formats seconds/minutes/hours/days in the past", () => {
    expect(formatRelativeTime(new Date(2026, 2, 23, 11, 59, 30).toISOString(), "en", now)).toBe("30 seconds ago");
    expect(formatRelativeTime(new Date(2026, 2, 23, 11, 55, 0).toISOString(), "en", now)).toBe("5 minutes ago");
    expect(formatRelativeTime(new Date(2026, 2, 23, 9, 0, 0).toISOString(), "en", now)).toBe("3 hours ago");
    expect(formatRelativeTime(new Date(2026, 2, 20, 12, 0, 0).toISOString(), "en", now)).toBe("3 days ago");
  });

  it("reads as now for sub-second differences", () => {
    expect(formatRelativeTime(now.toISOString(), "en", now)).toBe("now");
  });

  it("uses the locale's natural wording for non-English locales", () => {
    const out = formatRelativeTime(new Date(2026, 2, 23, 9, 0, 0).toISOString(), "de", now);
    expect(out.toLowerCase()).toContain("stunden");
  });

  it("defaults to the browser locale when none is passed", () => {
    expect(formatRelativeTime(new Date(2026, 2, 23, 9, 0, 0).toISOString(), undefined, now)).toContain("3");
  });
});
