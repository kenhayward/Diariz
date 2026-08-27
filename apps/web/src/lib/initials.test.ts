import { describe, it, expect } from "vitest";
import { initialsFromEmail, initialsFromName } from "./initials";

describe("initialsFromName", () => {
  it("uses the first and last word", () => {
    expect(initialsFromName("Ada Lovelace")).toBe("AL");
    expect(initialsFromName("Grace Brewster Hopper")).toBe("GH");
  });
  it("falls back to two letters of a single name", () => {
    expect(initialsFromName("Madonna")).toBe("MA");
  });
  it("returns ? for empty/null", () => {
    expect(initialsFromName(null)).toBe("?");
    expect(initialsFromName("   ")).toBe("?");
  });
});

describe("initialsFromEmail", () => {
  it("uses two name parts when present", () => {
    expect(initialsFromEmail("jane.doe@example.com")).toBe("JD");
    expect(initialsFromEmail("ada_lovelace@x.io")).toBe("AL");
    expect(initialsFromEmail("a-b@x.io")).toBe("AB");
  });

  it("falls back to the first two letters of a single part", () => {
    expect(initialsFromEmail("ada@lovelace.com")).toBe("AD");
    expect(initialsFromEmail("x@y.com")).toBe("X");
  });

  it("returns ? for empty/null input", () => {
    expect(initialsFromEmail(null)).toBe("?");
    expect(initialsFromEmail("")).toBe("?");
  });
});
