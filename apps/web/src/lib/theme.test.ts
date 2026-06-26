import { describe, it, expect, beforeEach } from "vitest";
import { resolveTheme, applyTheme, getStoredTheme, setStoredTheme } from "./theme";

describe("theme", () => {
  beforeEach(() => localStorage.clear());

  it("resolveTheme maps choice + OS preference to a concrete theme", () => {
    expect(resolveTheme("light", true)).toBe("light");
    expect(resolveTheme("dark", false)).toBe("dark");
    expect(resolveTheme("auto", true)).toBe("dark");
    expect(resolveTheme("auto", false)).toBe("light");
  });

  it("getStoredTheme defaults to auto and round-trips via setStoredTheme", () => {
    expect(getStoredTheme()).toBe("auto");
    setStoredTheme("dark");
    expect(getStoredTheme()).toBe("dark");
  });

  it("applyTheme toggles the .dark class on the given root", () => {
    const root = document.createElement("html");
    applyTheme("dark", root, false);
    expect(root.classList.contains("dark")).toBe(true);
    applyTheme("light", root, true);
    expect(root.classList.contains("dark")).toBe(false);
    applyTheme("auto", root, true);
    expect(root.classList.contains("dark")).toBe(true);
  });
});
