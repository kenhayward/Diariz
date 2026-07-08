import { describe, expect, it } from "vitest";
import { fontSizeFor, recordingsForTags } from "./tagCloud";
import type { RecordingSummary, TagCloudEntry } from "./types";

function rec(id: string): RecordingSummary {
  return {
    id,
    title: `Rec ${id}`,
    name: null,
    source: 0,
    durationMs: 1000,
    status: 3,
    createdAt: "2026-07-01T10:00:00Z",
    sectionId: null,
    sectionName: null,
    hasActions: false,
    hasAudio: true,
    calendarEventId: null,
  } as unknown as RecordingSummary;
}

function tag(name: string, weight: number, recordingIds: string[]): TagCloudEntry {
  return { tag: name, count: recordingIds.length, weight, recordingIds };
}

describe("fontSizeFor", () => {
  it("maps the minimum weight to minPx and the maximum to maxPx", () => {
    expect(fontSizeFor(0.5, 0.5, 4)).toBe(12);
    expect(fontSizeFor(4, 0.5, 4)).toBe(28);
  });

  it("is monotonic in weight", () => {
    const lo = fontSizeFor(1, 0.5, 4);
    const mid = fontSizeFor(2, 0.5, 4);
    const hi = fontSizeFor(3, 0.5, 4);
    expect(lo).toBeLessThan(mid);
    expect(mid).toBeLessThan(hi);
  });

  it("returns the midpoint when every weight is equal (including a single tag)", () => {
    expect(fontSizeFor(1.5, 1.5, 1.5)).toBe(20);
  });

  it("log-scales so one runaway tag does not flatten the rest", () => {
    // With a 10x outlier, a mid-weight tag must sit well above the floor (linear would give ~13px).
    const size = fontSizeFor(1, 0.5, 10);
    const linear = 12 + ((1 - 0.5) / (10 - 0.5)) * (28 - 12);
    expect(size).toBeGreaterThan(Math.round(linear));
  });

  it("honours custom pixel bounds", () => {
    expect(fontSizeFor(4, 0.5, 4, 16, 48)).toBe(48);
    expect(fontSizeFor(0.5, 0.5, 4, 16, 48)).toBe(16);
  });
});

describe("recordingsForTags", () => {
  const recordings = [rec("a"), rec("b"), rec("c"), rec("d")];
  const tags = [
    tag("Budget Planning", 1.4, ["a", "c"]),
    tag("Vendor Selection", 0.4, ["c", "d"]),
  ];

  it("with a selected tag, keeps only that tag's recordings in list order", () => {
    const out = recordingsForTags(recordings, tags, "Budget Planning");
    expect(out.map((r) => r.id)).toEqual(["a", "c"]);
  });

  it("with no selection, returns the union of all tagged recordings in list order", () => {
    const out = recordingsForTags(recordings, tags, null);
    expect(out.map((r) => r.id)).toEqual(["a", "c", "d"]); // "b" is untagged
  });

  it("returns empty for an unknown tag", () => {
    expect(recordingsForTags(recordings, tags, "Nope")).toEqual([]);
  });

  it("silently drops ids missing from the cached recordings list", () => {
    const stale = [tag("Old", 0.5, ["gone", "a"])];
    expect(recordingsForTags(recordings, stale, "Old").map((r) => r.id)).toEqual(["a"]);
  });
});
