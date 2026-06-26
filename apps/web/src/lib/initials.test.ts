import { describe, it, expect } from "vitest";
import { initialsFromEmail } from "./initials";

describe("initialsFromEmail", () => {
  it("uses two name parts when present", () => {
    expect(initialsFromEmail("jane.doe@example.com")).toBe("JD");
    expect(initialsFromEmail("ken_hayward@x.io")).toBe("KH");
    expect(initialsFromEmail("a-b@x.io")).toBe("AB");
  });

  it("falls back to the first two letters of a single part", () => {
    expect(initialsFromEmail("ken@stocks.com")).toBe("KE");
    expect(initialsFromEmail("x@y.com")).toBe("X");
  });

  it("returns ? for empty/null input", () => {
    expect(initialsFromEmail(null)).toBe("?");
    expect(initialsFromEmail("")).toBe("?");
  });
});
