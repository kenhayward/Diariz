import { describe, it, expect } from "vitest";
import { groupBySection, facetsOf, applyFilters, UNGROUPED_GROUP } from "./searchResults";
import type { RecordingSearchHit } from "./types";

const hit = (over: Partial<RecordingSearchHit>): RecordingSearchHit => ({
  recordingId: "r",
  name: "Rec",
  createdAt: "2026-06-26T12:00:00Z",
  durationMs: 1000,
  sectionId: null,
  sectionName: null,
  breadcrumb: [],
  snippet: "text",
  snippetStartMs: 0,
  speakerName: null,
  score: 0.5,
  ...over,
});

describe("groupBySection", () => {
  it("groups hits under the folder they live in", () => {
    const groups = groupBySection([
      hit({ recordingId: "a", sectionId: "cust", sectionName: "Customers", score: 0.9 }),
      hit({ recordingId: "b", sectionId: "pack", sectionName: "Packaging", score: 0.8 }),
      hit({ recordingId: "c", sectionId: "cust", sectionName: "Customers", score: 0.7 }),
    ]);
    expect(groups.map((g) => g.name)).toEqual(["Customers", "Packaging"]);
    expect(groups[0].hits.map((h) => h.recordingId)).toEqual(["a", "c"]);
  });

  // The server ranks by relevance; grouping must not quietly reorder the best hit away from the top.
  it("orders groups by their best hit, and hits within a group by score", () => {
    const groups = groupBySection([
      hit({ recordingId: "weak", sectionId: "a", sectionName: "Alpha", score: 0.2 }),
      hit({ recordingId: "strong", sectionId: "b", sectionName: "Beta", score: 0.9 }),
      hit({ recordingId: "mid", sectionId: "a", sectionName: "Alpha", score: 0.5 }),
    ]);
    expect(groups.map((g) => g.name)).toEqual(["Beta", "Alpha"]);
    expect(groups[1].hits.map((h) => h.recordingId)).toEqual(["mid", "weak"]);
  });

  it("puts hits with no folder in their own group, last", () => {
    const groups = groupBySection([
      hit({ recordingId: "loose", sectionId: null, score: 0.9 }),
      hit({ recordingId: "filed", sectionId: "cust", sectionName: "Customers", score: 0.1 }),
    ]);
    expect(groups[groups.length - 1].id).toBe(UNGROUPED_GROUP);
    expect(groups[groups.length - 1].hits.map((h) => h.recordingId)).toEqual(["loose"]);
  });

  it("is empty for no hits", () => {
    expect(groupBySection([])).toEqual([]);
  });
});

describe("facetsOf", () => {
  it("lists the distinct folders and speakers present in the hits", () => {
    const f = facetsOf([
      hit({ sectionId: "cust", sectionName: "Customers", speakerName: "Alice" }),
      hit({ sectionId: "cust", sectionName: "Customers", speakerName: "Bob" }),
      hit({ sectionId: "pack", sectionName: "Packaging", speakerName: "Alice" }),
    ]);
    expect(f.sections.map((s) => s.name)).toEqual(["Customers", "Packaging"]);
    expect(f.speakers).toEqual(["Alice", "Bob"]);
  });

  it("orders folders by how many hits they hold", () => {
    const f = facetsOf([
      hit({ sectionId: "rare", sectionName: "Rare" }),
      hit({ sectionId: "common", sectionName: "Common" }),
      hit({ sectionId: "common", sectionName: "Common" }),
    ]);
    expect(f.sections[0].name).toBe("Common");
  });

  it("ignores hits with no folder or no speaker", () => {
    const f = facetsOf([hit({ sectionId: null, speakerName: null })]);
    expect(f.sections).toEqual([]);
    expect(f.speakers).toEqual([]);
  });
});

describe("applyFilters", () => {
  const hits = [
    hit({ recordingId: "a", sectionId: "cust", sectionName: "Customers", speakerName: "Alice", createdAt: "2026-06-01T00:00:00Z" }),
    hit({ recordingId: "b", sectionId: "pack", sectionName: "Packaging", speakerName: "Bob", createdAt: "2026-07-01T00:00:00Z" }),
  ];

  it("passes everything through when nothing is set", () => {
    expect(applyFilters(hits, {})).toHaveLength(2);
  });

  it("filters by folder", () => {
    expect(applyFilters(hits, { sectionId: "cust" }).map((h) => h.recordingId)).toEqual(["a"]);
  });

  it("filters by speaker", () => {
    expect(applyFilters(hits, { speaker: "Bob" }).map((h) => h.recordingId)).toEqual(["b"]);
  });

  it("filters by date from", () => {
    expect(applyFilters(hits, { from: "2026-06-15" }).map((h) => h.recordingId)).toEqual(["b"]);
  });

  it("combines filters", () => {
    expect(applyFilters(hits, { sectionId: "cust", speaker: "Bob" })).toEqual([]);
  });
});
