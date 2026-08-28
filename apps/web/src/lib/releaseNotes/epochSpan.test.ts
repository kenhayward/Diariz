import { describe, it, expect } from "vitest";
import { epochSpan } from "./epochSpan";
import { EPOCHS, ARCHIVED_SPINE } from "./epochs";
import type { Epoch } from "./types";

const epoch = (from: string, to: string): Epoch => ({
  id: "x",
  title: "x",
  from,
  to,
  summary: "x",
});

const spine = [
  { version: "0.5.0", date: "2026-01-05" },
  { version: "0.4.1", date: "2026-01-04" },
  { version: "0.4.0", date: "2026-01-03" },
  { version: "0.3.0", date: "2026-01-01" },
];

describe("epochSpan", () => {
  it("counts the releases in the range and reports the dates at each end", () => {
    expect(epochSpan(epoch("0.4.0", "0.5.0"), spine)).toEqual({
      count: 3,
      earliest: "2026-01-03",
      latest: "2026-01-05",
    });
  });

  it("counts a single-release epoch as one", () => {
    expect(epochSpan(epoch("0.3.0", "0.3.0"), spine)).toEqual({
      count: 1,
      earliest: "2026-01-01",
      latest: "2026-01-01",
    });
  });

  it("returns null rather than throwing when a bound is not in the spine", () => {
    // Total by design: the page renders during a bad edit rather than blanking. epochs.test.ts is what
    // guarantees the bounds exist, and the list test below fails loudly if any epoch resolves to null,
    // so a real mistake still surfaces instead of quietly rendering an epoch with no dates.
    expect(epochSpan(epoch("9.9.9", "0.5.0"), spine)).toBeNull();
    expect(epochSpan(epoch("0.3.0", "9.9.9"), spine)).toBeNull();
  });

  it("resolves every real epoch against the real spine", () => {
    const unresolved = EPOCHS.filter((e) => epochSpan(e, ARCHIVED_SPINE) === null);
    expect(unresolved.map((e) => e.id)).toEqual([]);
  });

  it("accounts for every archived release exactly once across all epochs", () => {
    const total = EPOCHS.reduce((n, e) => n + (epochSpan(e, ARCHIVED_SPINE)?.count ?? 0), 0);
    expect(total).toBe(ARCHIVED_SPINE.length);
  });
});
