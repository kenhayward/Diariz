import { describe, it, expect } from "vitest";
import { hubCounts } from "./hubCounts";

const rec = (over: Partial<Parameters<typeof hubCounts>[0]> = {}) => ({
  durationMs: 60_000,
  speakers: [{ label: "SPEAKER_00" }, { label: "SPEAKER_01" }],
  current: { segments: [{ id: "s1" }, { id: "s2" }, { id: "s3" }] },
  actions: [{ completed: false }, { completed: false }, { completed: true }],
  ...over,
});

describe("hubCounts", () => {
  it("derives every tile's count from the recording and its side queries", () => {
    const counts = hubCounts(rec(), [{ id: "n1" }], [{ id: "a1" }, { id: "a2" }], [{ id: "f1" }]);
    expect(counts).toEqual({
      segments: 3,
      durationMs: 60_000,
      actionsOpen: 2,
      actionsDone: 1,
      speakers: 2,
      notes: 1,
      files: 2,
      formulaRuns: 1,
      screenshots: 0,
    });
  });

  it("counts screenshots from the shots side query", () => {
    const counts = hubCounts(rec(), [], [], [], [{ id: "sh1" }, { id: "sh2" }, { id: "sh3" }]);
    expect(counts.screenshots).toBe(3);
  });

  it("reports zero screenshots when no shots query is supplied", () => {
    expect(hubCounts(rec(), [], [], []).screenshots).toBe(0);
  });

  it("splits actions into open and done on `completed`", () => {
    const counts = hubCounts(rec({ actions: [{ completed: true }, { completed: true }] }), [], [], []);
    expect(counts.actionsOpen).toBe(0);
    expect(counts.actionsDone).toBe(2);
  });

  it("reports zero segments when the recording has no transcription yet", () => {
    expect(hubCounts(rec({ current: null }), [], [], []).segments).toBe(0);
  });

  it("is all zeroes for an empty recording", () => {
    const counts = hubCounts(
      rec({ durationMs: 0, speakers: [], current: null, actions: [] }),
      [],
      [],
      [],
    );
    expect(counts).toEqual({
      segments: 0,
      durationMs: 0,
      actionsOpen: 0,
      actionsDone: 0,
      speakers: 0,
      notes: 0,
      files: 0,
      formulaRuns: 0,
      screenshots: 0,
    });
  });
});
