import { describe, it, expect } from "vitest";
import { SPEAKER_PALETTE, speakerColorMap, speakerColorFor } from "./speakerColors";

describe("speakerColorMap", () => {
  it("assigns a distinct palette colour to each label while the palette lasts", () => {
    const map = speakerColorMap(["SPEAKER_00", "SPEAKER_01", "SPEAKER_02"]);
    const colors = [...map.values()];
    expect(new Set(colors).size).toBe(3);
    expect(colors.every((c) => SPEAKER_PALETTE.includes(c))).toBe(true);
  });

  it("assigns colours by sorted label order, so the same speaker keeps its colour regardless of input order", () => {
    const a = speakerColorMap(["SPEAKER_02", "SPEAKER_00", "SPEAKER_01"]);
    const b = speakerColorMap(["SPEAKER_00", "SPEAKER_01", "SPEAKER_02"]);
    expect(a.get("SPEAKER_01")).toBe(b.get("SPEAKER_01"));
  });

  it("gives the first sorted label the first palette entry", () => {
    expect(speakerColorMap(["SPEAKER_01", "SPEAKER_00"]).get("SPEAKER_00")).toBe(SPEAKER_PALETTE[0]);
  });

  it("ignores duplicate labels", () => {
    const map = speakerColorMap(["SPEAKER_00", "SPEAKER_00", "SPEAKER_01"]);
    expect(map.size).toBe(2);
  });

  it("wraps around once there are more speakers than palette entries", () => {
    const labels = Array.from({ length: SPEAKER_PALETTE.length + 1 }, (_, i) => `S${String(i).padStart(2, "0")}`);
    const map = speakerColorMap(labels);
    expect(map.size).toBe(labels.length);
    // The one past the end of the palette reuses the first colour.
    expect(map.get(labels[SPEAKER_PALETTE.length])).toBe(SPEAKER_PALETTE[0]);
  });

  it("is empty for no labels", () => {
    expect(speakerColorMap([]).size).toBe(0);
  });
});

describe("speakerColorFor", () => {
  it("returns the label's colour from the set of all labels", () => {
    const labels = ["SPEAKER_00", "SPEAKER_01"];
    expect(speakerColorFor("SPEAKER_00", labels)).toBe(SPEAKER_PALETTE[0]);
    expect(speakerColorFor("SPEAKER_01", labels)).toBe(SPEAKER_PALETTE[1]);
  });

  it("falls back to the first palette colour for a label that isn't in the list", () => {
    expect(speakerColorFor("SPEAKER_99", ["SPEAKER_00"])).toBe(SPEAKER_PALETTE[0]);
  });
});
