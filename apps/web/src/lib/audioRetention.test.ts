import { describe, it, expect } from "vitest";
import { retentionDaysLeft } from "./audioRetention";

const now = new Date("2026-06-30T12:00:00Z");

describe("retentionDaysLeft", () => {
  it("is null when no deletion is scheduled (auto-delete off, or the audio is protected)", () => {
    expect(retentionDaysLeft(null, now)).toBeNull();
  });

  it("counts the whole days left until the audio is deleted", () => {
    expect(retentionDaysLeft("2026-07-16T12:00:00Z", now)).toBe(16);
  });

  it("rounds a part-day up, so the last day still reads '1d left' rather than '0d left'", () => {
    expect(retentionDaysLeft("2026-07-01T06:00:00Z", now)).toBe(1);
  });

  it("is zero once the scheduled date has passed (the nightly job just hasn't run yet)", () => {
    expect(retentionDaysLeft("2026-06-29T12:00:00Z", now)).toBe(0);
  });

  it("is zero at exactly the scheduled moment", () => {
    expect(retentionDaysLeft("2026-06-30T12:00:00Z", now)).toBe(0);
  });
});
