import { describe, it, expect } from "vitest";
import { isSpeakerAssigned, allSpeakersAssigned } from "./speakers";
import type { SpeakerInfo } from "./types";

const sp = (over: Partial<SpeakerInfo>): SpeakerInfo => ({
  label: "SPEAKER_00",
  displayName: "SPEAKER_00",
  profileId: null,
  identifiedAuto: false,
  ...over,
});

describe("isSpeakerAssigned", () => {
  it("is false for an anonymous speaker (name equals its label)", () => {
    expect(isSpeakerAssigned(sp({}))).toBe(false);
  });

  it("is true when renamed (display name differs from the label)", () => {
    expect(isSpeakerAssigned(sp({ displayName: "Alice" }))).toBe(true);
  });

  it("is true when linked to a profile", () => {
    expect(isSpeakerAssigned(sp({ profileId: "abc", displayName: "Alice" }))).toBe(true);
  });
});

describe("allSpeakersAssigned", () => {
  it("is false when there are no speakers", () => {
    expect(allSpeakersAssigned([])).toBe(false);
  });

  it("is false when any speaker is still anonymous", () => {
    expect(
      allSpeakersAssigned([sp({ displayName: "Alice" }), sp({ label: "SPEAKER_01", displayName: "SPEAKER_01" })]),
    ).toBe(false);
  });

  it("is true when every speaker is assigned", () => {
    expect(
      allSpeakersAssigned([
        sp({ displayName: "Alice" }),
        sp({ label: "SPEAKER_01", profileId: "p1", displayName: "Bob" }),
      ]),
    ).toBe(true);
  });
});
