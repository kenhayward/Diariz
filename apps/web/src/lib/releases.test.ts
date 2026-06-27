import { describe, it, expect } from "vitest";
import { RELEASES } from "./releases";
import { APP_VERSION } from "./version";

describe("releases", () => {
  it("has at least one release, newest first, with the top matching the app version", () => {
    expect(RELEASES.length).toBeGreaterThan(0);
    expect(RELEASES[0].version).toBe(APP_VERSION);

    const dates = RELEASES.map((r) => r.date);
    const sorted = [...dates].sort().reverse();
    expect(dates).toEqual(sorted); // non-increasing by date
  });

  it("every entry has a version, date, headline and summary", () => {
    for (const r of RELEASES) {
      expect(r.version).toMatch(/^\d+\.\d+\.\d+$/);
      expect(r.date).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(r.headline.length).toBeGreaterThan(0);
      expect(r.summary.length).toBeGreaterThan(0);
    }
  });

  it("has no duplicate versions", () => {
    const versions = RELEASES.map((r) => r.version);
    expect(new Set(versions).size).toBe(versions.length);
  });
});
