import { describe, expect, it } from "vitest";
import { fontSizeFor, recordingsForTags, tagColor, topTagsByCount } from "./tagCloud";
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
    expect(fontSizeFor(0.5, 0.5, 4)).toBe(11); // compact defaults so the cloud doesn't run to many pages
    expect(fontSizeFor(4, 0.5, 4)).toBe(22);
  });

  it("is monotonic in weight", () => {
    const lo = fontSizeFor(1, 0.5, 4);
    const mid = fontSizeFor(2, 0.5, 4);
    const hi = fontSizeFor(3, 0.5, 4);
    expect(lo).toBeLessThan(mid);
    expect(mid).toBeLessThan(hi);
  });

  it("returns the midpoint when every weight is equal (including a single tag)", () => {
    expect(fontSizeFor(1.5, 1.5, 1.5)).toBe(17); // (11 + 22) / 2, rounded
  });

  it("log-scales so one runaway tag does not flatten the rest", () => {
    // With a 10x outlier, a mid-weight tag must sit well above the floor (linear would be ~12px).
    const size = fontSizeFor(1, 0.5, 10);
    const linear = 11 + ((1 - 0.5) / (10 - 0.5)) * (22 - 11);
    expect(size).toBeGreaterThan(Math.round(linear));
  });

  it("honours custom pixel bounds (the expanded modal passes larger ones)", () => {
    expect(fontSizeFor(4, 0.5, 4, 14, 40)).toBe(40);
    expect(fontSizeFor(0.5, 0.5, 4, 14, 40)).toBe(14);
  });
});

describe("tagColor", () => {
  it("returns an hsl string that varies with weight", () => {
    const lo = tagColor(0.5, 0.5, 4);
    const hi = tagColor(4, 0.5, 4);
    expect(lo).toMatch(/^hsl\(/);
    expect(hi).toMatch(/^hsl\(/);
    expect(lo).not.toEqual(hi); // heavier tags get a different (warmer) colour
  });

  it("collapses to a single mid colour when all weights are equal", () => {
    expect(tagColor(2, 2, 2)).toMatch(/^hsl\(/);
  });
});

describe("topTagsByCount", () => {
  const tags: TagCloudEntry[] = [
    { tag: "Rare", count: 1, weight: 0.9, recordingIds: ["a"] },
    { tag: "Common", count: 5, weight: 0.3, recordingIds: ["a", "b", "c", "d", "e"] },
    { tag: "Mid", count: 3, weight: 0.8, recordingIds: ["a", "b", "c"] },
  ];

  it("keeps the N tags with the highest recording count", () => {
    const out = topTagsByCount(tags, 2);
    expect(out.map((t) => t.tag)).toEqual(["Common", "Mid"]); // by count desc, not weight
  });

  it("breaks count ties by weight descending", () => {
    const tied: TagCloudEntry[] = [
      { tag: "LightTie", count: 2, weight: 0.2, recordingIds: ["a", "b"] },
      { tag: "HeavyTie", count: 2, weight: 0.9, recordingIds: ["a", "b"] },
    ];
    expect(topTagsByCount(tied, 1).map((t) => t.tag)).toEqual(["HeavyTie"]);
  });

  it("returns all tags when the limit meets or exceeds the total", () => {
    expect(topTagsByCount(tags, 99)).toHaveLength(3);
  });

  it("returns an empty list for a non-positive limit", () => {
    expect(topTagsByCount(tags, 0)).toEqual([]);
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
