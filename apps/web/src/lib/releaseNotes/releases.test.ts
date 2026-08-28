import { describe, it, expect } from "vitest";
import { RECENT } from "./current";
import { ARCHIVE } from "./archive";
import { APP_VERSION } from "../version";

/// The release history is split across two modules so the archive can stay out of the initial bundle:
/// `current.ts` holds the releases since the last closed epoch and is eager, `archive.ts` holds everything
/// older and is reached only through `loadArchive()`. Splitting one array into two creates a class of bug
/// the old single-array tests could not have: an entry that falls down the gap between the halves, or is
/// duplicated across both. These tests are written against the **union**, so a hole shows up as a failure
/// rather than as a shorter list nobody counted.
const ALL = [...RECENT, ...ARCHIVE];

describe("release history", () => {
  it("starts at the version the app reports", () => {
    expect(RECENT[0].version).toBe(APP_VERSION);
  });

  it("runs unbroken from the newest release back to the first one ever shipped", () => {
    // 0.1.0 is the first tagged release and can never stop being the last element: it anchors the tail,
    // so truncating the archive fails here instead of silently shortening the page.
    expect(ALL[ALL.length - 1].version).toBe("0.1.0");
  });

  it("has no duplicate versions across the two halves", () => {
    const versions = ALL.map((r) => r.version);
    const seen = new Set(versions);
    expect(seen.size).toBe(versions.length);
  });

  it("is ordered newest first across the two halves", () => {
    const dates = ALL.map((r) => r.date);
    expect(dates).toEqual([...dates].sort().reverse());
  });

  it("does not overlap: every archived release is older than every current one", () => {
    const oldestCurrent = RECENT[RECENT.length - 1].date;
    const newestArchived = ARCHIVE[0].date;
    expect(newestArchived <= oldestCurrent).toBe(true);

    const currentVersions = new Set(RECENT.map((r) => r.version));
    const both = ARCHIVE.filter((r) => currentVersions.has(r.version));
    expect(both.map((r) => r.version)).toEqual([]);
  });

  it("keeps the eager half small enough to be worth loading eagerly", () => {
    // A safety net, not the trigger: an epoch is closed when an arc finishes, and the historical epochs
    // average 16 releases. This only fires if closing one is forgotten for months.
    expect(RECENT.length).toBeLessThanOrEqual(80);
  });

  it("gives every entry a version, date, headline and summary", () => {
    const malformed = ALL.filter(
      (r) =>
        !/^\d+\.\d+\.\d+$/.test(r.version) ||
        !/^\d{4}-\d{2}-\d{2}$/.test(r.date) ||
        r.headline.length === 0 ||
        r.summary.length === 0,
    );
    expect(malformed.map((r) => r.version)).toEqual([]);
  });
});
