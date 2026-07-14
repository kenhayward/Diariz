import { describe, it, expect } from "vitest";
import {
  serializeMeetingType, parseMeetingType, resolveFormulaNames, type MeetingTypeExport,
} from "./meetingTypeIo";

const sample: MeetingTypeExport = {
  groupName: "Mine",
  title: "Interview",
  overview: "A hiring interview.",
  icon: "chat",
  color: "#5C6BC0",
  primaryFormulaName: "Interview minutes",
  additionalFormulaNames: ["Follow-up email"],
};

describe("meetingTypeIo", () => {
  it("round-trips a template through serialize -> parse", () => {
    expect(parseMeetingType(serializeMeetingType(sample))).toEqual(sample);
  });

  it("tags the export with a format marker", () => {
    expect(JSON.parse(serializeMeetingType(sample))["diariz-meeting-type"]).toBe(1);
  });

  it("throws on invalid JSON", () => {
    expect(() => parseMeetingType("{not json")).toThrow();
  });

  it("throws on something that is not a meeting type at all", () => {
    expect(() => parseMeetingType(JSON.stringify({ foo: 1 }))).toThrow();
  });

  it("defaults optional presentation fields and coerces missing strings", () => {
    const parsed = parseMeetingType(JSON.stringify({ title: "Only title" }));
    expect(parsed).toEqual({
      groupName: "",
      title: "Only title",
      overview: "",
      icon: "document",
      color: "#5C6BC0",
      primaryFormulaName: null,
      additionalFormulaNames: [],
    });
  });

  it("ignores extra fields (id, isPlatform) from a raw meeting type", () => {
    const parsed = parseMeetingType(
      JSON.stringify({ id: "abc", isPlatform: true, canEdit: true, ...sample }),
    );
    expect(parsed).toEqual(sample);
  });
});

// A meeting type points at a formula, and formula IDs mean nothing on another instance - so the export carries
// NAMES and the import resolves them against whatever the target instance has.
describe("resolveFormulaNames", () => {
  const formulas = [
    { id: "f1", name: "Interview minutes" },
    { id: "f2", name: "Follow-up email" },
  ];

  it("resolves names to the ids on this instance", () => {
    expect(resolveFormulaNames(["Interview minutes", "Follow-up email"], formulas)).toEqual({
      ids: ["f1", "f2"],
      missing: [],
    });
  });

  it("matches case-insensitively", () => {
    expect(resolveFormulaNames(["interview MINUTES"], formulas).ids).toEqual(["f1"]);
  });

  // Reported, not silently dropped: a template pointing at nothing would generate nothing.
  it("reports a name this instance does not have", () => {
    expect(resolveFormulaNames(["Interview minutes", "Nope"], formulas)).toEqual({
      ids: ["f1"],
      missing: ["Nope"],
    });
  });

  it("is empty for no names", () => {
    expect(resolveFormulaNames([], formulas)).toEqual({ ids: [], missing: [] });
  });
});
