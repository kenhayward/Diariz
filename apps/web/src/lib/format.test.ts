import { describe, it, expect } from "vitest";
import { formatBytes, storagePercent, bytesToGb, gbToBytes, formatDuration } from "./format";

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

describe("GB conversion", () => {
  it("round-trips whole gigabytes", () => {
    expect(gbToBytes(5)).toBe(5 * 1024 ** 3);
    expect(bytesToGb(5 * 1024 ** 3)).toBe(5);
  });
});
