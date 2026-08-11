import { describe, it, expect } from "vitest";
import { parseSort, sortRecordings, DEFAULT_SORT, type ListSort } from "./listSort";
import type { RecordingSummary } from "./types";

const base: RecordingSummary = {
  id: "a",
  title: "Mic 6/26/2026",
  name: "Alpha",
  source: "Microphone",
  durationMs: 1000,
  status: "Transcribed",
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};

// Manual order is deliberately neither alphabetical nor chronological, so every key below moves the list.
const items: RecordingSummary[] = [
  { ...base, id: "a", name: "Beta", durationMs: 3000, createdAt: new Date("2026-01-02T09:00:00Z").toISOString() },
  { ...base, id: "b", name: "Alpha", durationMs: 1000, createdAt: new Date("2026-03-04T09:00:00Z").toISOString() },
  { ...base, id: "c", name: "Gamma", durationMs: 2000, createdAt: new Date("2026-02-03T09:00:00Z").toISOString() },
];

const ids = (sort: ListSort) => sortRecordings(items, sort).map((r) => r.id);

describe("sortRecordings", () => {
  it("leaves the manual order untouched in both directions", () => {
    expect(ids({ key: "manual", dir: "asc" })).toEqual(["a", "b", "c"]);
    expect(ids({ key: "manual", dir: "desc" })).toEqual(["a", "b", "c"]);
  });

  it("sorts by name", () => {
    expect(ids({ key: "name", dir: "asc" })).toEqual(["b", "a", "c"]);
    expect(ids({ key: "name", dir: "desc" })).toEqual(["c", "a", "b"]);
  });

  it("sorts by date", () => {
    expect(ids({ key: "date", dir: "asc" })).toEqual(["a", "c", "b"]);
    expect(ids({ key: "date", dir: "desc" })).toEqual(["b", "c", "a"]);
  });

  it("sorts by duration", () => {
    expect(ids({ key: "duration", dir: "asc" })).toEqual(["b", "c", "a"]);
    expect(ids({ key: "duration", dir: "desc" })).toEqual(["a", "c", "b"]);
  });

  // The rows display `name ?? title`, so the sort must agree with what the reader can see.
  it("falls back to the title when a recording has no name", () => {
    const unnamed = [
      { ...base, id: "x", name: null, title: "Zulu" },
      { ...base, id: "y", name: null, title: "Alpha" },
    ];
    expect(sortRecordings(unnamed, { key: "name", dir: "asc" }).map((r) => r.id)).toEqual(["y", "x"]);
  });

  // The panel keeps a separate unsorted list for its reorder writes; a mutating sort would corrupt it.
  it("does not mutate its input", () => {
    const input = [...items];
    sortRecordings(input, { key: "name", dir: "asc" });
    expect(input.map((r) => r.id)).toEqual(["a", "b", "c"]);
  });
});

describe("parseSort", () => {
  it("reads a stored setting back", () => {
    expect(parseSort(JSON.stringify({ key: "date", dir: "desc" }))).toEqual({ key: "date", dir: "desc" });
  });

  // A first visit, a value from an older build, and a hand-edited key all have to land somewhere safe.
  it.each([null, "", "not json", '{"key":"colour","dir":"asc"}', '{"key":"name","dir":"sideways"}', '"name"'])(
    "falls back to the default for %s",
    (raw) => {
      expect(parseSort(raw as string | null)).toEqual(DEFAULT_SORT);
    },
  );
});
