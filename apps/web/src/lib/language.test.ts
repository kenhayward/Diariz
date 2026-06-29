import { describe, it, expect, beforeEach } from "vitest";
import { getStoredLanguage, setStoredLanguage } from "./language";

describe("language storage", () => {
  beforeEach(() => localStorage.clear());

  it("returns null when nothing is stored", () => {
    expect(getStoredLanguage()).toBeNull();
  });

  it("round-trips a stored code", () => {
    setStoredLanguage("es");
    expect(getStoredLanguage()).toBe("es");
  });

  it("clears the stored code when set to null or blank", () => {
    setStoredLanguage("fr");
    setStoredLanguage(null);
    expect(getStoredLanguage()).toBeNull();
    setStoredLanguage("de");
    setStoredLanguage("   ");
    expect(getStoredLanguage()).toBeNull();
  });
});
