import { describe, it, expect } from "vitest";
import { serializeMeetingType, parseMeetingType, type MeetingTypeExport } from "./meetingTypeIo";

const sample: MeetingTypeExport = {
  groupName: "Mine",
  title: "Interview",
  overview: "A hiring interview.",
  icon: "chat",
  color: "#5C6BC0",
  content: { sections: [{ level: 1, title: "Details", blocks: [{ kind: "boilerplate", text: "Date: ", breakAfter: "none" }] }] },
};

describe("meetingTypeIo", () => {
  it("round-trips a template through serialize -> parse", () => {
    const parsed = parseMeetingType(serializeMeetingType(sample));
    expect(parsed).toEqual(sample);
  });

  it("tags the export with a format marker", () => {
    expect(JSON.parse(serializeMeetingType(sample))["diariz-meeting-type"]).toBe(1);
  });

  it("throws on invalid JSON", () => {
    expect(() => parseMeetingType("{not json")).toThrow();
  });

  it("throws when content.sections is missing", () => {
    expect(() => parseMeetingType(JSON.stringify({ title: "x", content: {} }))).toThrow();
  });

  it("defaults optional presentation fields and coerces missing strings", () => {
    const parsed = parseMeetingType(JSON.stringify({ title: "Only title", content: { sections: [] } }));
    expect(parsed).toEqual({
      groupName: "",
      title: "Only title",
      overview: "",
      icon: "document",
      color: "#5C6BC0",
      content: { sections: [] },
    });
  });

  it("ignores extra fields (id, isPlatform) from a raw meeting type", () => {
    const parsed = parseMeetingType(
      JSON.stringify({ id: "abc", isPlatform: true, canEdit: true, ...sample }),
    );
    expect(parsed).toEqual(sample);
  });
});
