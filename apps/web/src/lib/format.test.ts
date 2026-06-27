import { describe, it, expect } from "vitest";
import { formatBytes, storagePercent, bytesToGb, gbToBytes } from "./format";

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
