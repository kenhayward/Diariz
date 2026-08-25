import { describe, expect, it } from "vitest";
import { rowVerdict, similarityPercent, sortKey } from "./voiceprintVerdict";
import type { SampleDiagnosis } from "./types";

function diagnosis(over: Partial<SampleDiagnosis> = {}): SampleDiagnosis {
  return {
    voiceSampleId: "vs1", speakerId: "sp1", recordingId: "r1", recordingName: "Standup",
    speakerLabel: "SPEAKER_00", nearestSiblingDistance: 0.1, distanceToOthers: 0.12,
    verdict: "Core", isTraining: true, ...over,
  };
}

describe("similarityPercent", () => {
  it("reads high when the two voices are alike", () => {
    // The defect this fixes: the tab printed the cosine *distance* under a label that reads as a
    // percentage match, so the worst row on the screen showed the largest, most reassuring number.
    expect(similarityPercent(0.18)).toBe(82);
    expect(similarityPercent(0.82)).toBe(18);
  });

  it("never goes below zero", () => {
    // Cosine distance runs to 2 for opposed vectors. "-13% similar" is not a thing to show anyone.
    expect(similarityPercent(1.4)).toBe(0);
  });
});

describe("rowVerdict", () => {
  it("passes the server's three verdicts through", () => {
    expect(rowVerdict(diagnosis({ verdict: "Core" }), true)).toBe("core");
    expect(rowVerdict(diagnosis({ verdict: "Variant" }), true)).toBe("variant");
    expect(rowVerdict(diagnosis({ verdict: "Alone" }), true)).toBe("alone");
  });

  it("calls an unlinked row unlinked whatever its diagnosis says", () => {
    // Its speaker no longer names this person, which is a fact about the link rather than a judgement
    // about the voice. Reporting it as an outlier would invite someone to fix the wrong thing.
    expect(rowVerdict(diagnosis({ verdict: "Alone" }), false)).toBe("unlinked");
    expect(rowVerdict(diagnosis({ verdict: "Core" }), false)).toBe("unlinked");
  });

  it("has nothing to say about a recording that trains nothing", () => {
    // Automatic identification links a speaker without creating a sample, so most rows have no
    // diagnosis at all. That is not a problem, and must not read as one.
    expect(rowVerdict(undefined, true)).toBe("only");
  });
});

describe("sortKey", () => {
  it("puts the rows worth acting on at the top", () => {
    // In the live report the one row that mattered was third, under two healthy ones.
    const order = (["core", "only", "alone", "variant", "unlinked"] as const)
      .slice()
      .sort((a, b) => sortKey(a) - sortKey(b));

    expect(order[0]).toBe("alone");
    expect(order[1]).toBe("unlinked");
  });

  it("does not separate the rows that need no attention", () => {
    // Core, Variant and Only are all "nothing to do here". Ranking them against each other would
    // imply a difference the user is meant to act on.
    expect(sortKey("core")).toBe(sortKey("variant"));
    expect(sortKey("core")).toBe(sortKey("only"));
  });
});
