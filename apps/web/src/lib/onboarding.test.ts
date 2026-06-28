import { describe, it, expect, beforeEach } from "vitest";
import { hasOnboarded, setOnboarded, emptyStateCopy, TOUR_STEPS, ONBOARDED_KEY } from "./onboarding";

describe("onboarding state", () => {
  beforeEach(() => localStorage.clear());

  it("defaults to not-onboarded, then round-trips through localStorage", () => {
    expect(hasOnboarded()).toBe(false);
    setOnboarded(true);
    expect(localStorage.getItem(ONBOARDED_KEY)).toBe("true");
    expect(hasOnboarded()).toBe(true);
    setOnboarded(false);
    expect(hasOnboarded()).toBe(false);
  });
});

describe("emptyStateCopy", () => {
  it("welcomes a brand-new user and offers the tour", () => {
    const c = emptyStateCopy(0);
    expect(c.showTour).toBe(true);
    expect(c.title).toMatch(/welcome/i);
  });

  it("prompts a returning user to pick a recording (no tour)", () => {
    const c = emptyStateCopy(3);
    expect(c.showTour).toBe(false);
    expect(c.title).toMatch(/select/i);
  });
});

describe("TOUR_STEPS", () => {
  it("is non-empty with unique targets", () => {
    expect(TOUR_STEPS.length).toBeGreaterThan(0);
    const targets = TOUR_STEPS.map((s) => s.target);
    expect(new Set(targets).size).toBe(targets.length);
    for (const s of TOUR_STEPS) {
      expect(s.title).toBeTruthy();
      expect(s.body).toBeTruthy();
    }
  });
});
