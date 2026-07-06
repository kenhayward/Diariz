import { describe, it, expect } from "vitest";
import { groupMeetingTypes, selectedMeetingType } from "./meetingTypes";
import type { MeetingType } from "./types";

function mt(id: string, groupName: string, extra: Partial<MeetingType> = {}): MeetingType {
  return {
    id,
    isPlatform: true,
    canEdit: false,
    groupName,
    title: id,
    overview: "",
    icon: "document",
    color: "#5C6BC0",
    content: { sections: [] },
    isDefault: false,
    ...extra,
  };
}

describe("groupMeetingTypes", () => {
  it("groups by template group preserving order", () => {
    const groups = groupMeetingTypes([
      mt("general", "Standard", { isDefault: true }),
      mt("cadence", "Team"),
      mt("weekly", "Team"),
      mt("customer", "Customer"),
    ]);

    expect(groups.map(([g, list]) => [g, list.map((t) => t.id)])).toEqual([
      ["Standard", ["general"]],
      ["Team", ["cadence", "weekly"]],
      ["Customer", ["customer"]],
    ]);
  });

  it("is empty for no types", () => {
    expect(groupMeetingTypes([])).toEqual([]);
  });
});

describe("selectedMeetingType", () => {
  const types = [mt("general", "Standard", { isDefault: true }), mt("cadence", "Team")];

  it("returns the explicitly-applied type", () => {
    expect(selectedMeetingType(types, "cadence")?.id).toBe("cadence");
  });

  it("falls back to the General default when no type is applied", () => {
    expect(selectedMeetingType(types, null)?.id).toBe("general");
    expect(selectedMeetingType(types, undefined)?.id).toBe("general");
  });

  it("falls back to the default when the applied id is unknown/stale", () => {
    expect(selectedMeetingType(types, "gone")?.id).toBe("general");
  });

  it("is null when there are no types", () => {
    expect(selectedMeetingType([], null)).toBeNull();
  });
});
