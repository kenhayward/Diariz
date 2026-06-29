import { describe, it, expect, beforeEach } from "vitest";
import { getStoredLanguage, resolveLanguage, setStoredLanguage } from "./language";

describe("resolveLanguage", () => {
  const available = ["en", "es", "fr", "de"];

  it("prefers a stored language that has a catalog", () => {
    expect(resolveLanguage("fr", ["en-US"], available)).toBe("fr");
  });

  it("ignores a stored language with no catalog and falls through to the browser", () => {
    expect(resolveLanguage("ja", ["es-ES"], available)).toBe("es");
  });

  it("matches the browser language by base subtag", () => {
    expect(resolveLanguage(null, ["de-AT", "en"], available)).toBe("de");
  });

  it("falls back to English when nothing matches", () => {
    expect(resolveLanguage(null, ["ja-JP", "ko"], available)).toBe("en");
    expect(resolveLanguage(null, [], available)).toBe("en");
  });
});

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
