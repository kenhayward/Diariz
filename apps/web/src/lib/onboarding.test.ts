import { describe, it, expect, beforeEach } from "vitest";
import { hasOnboarded, setOnboarded, TOUR_STEPS, ONBOARDED_KEY } from "./onboarding";

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

describe("TOUR_STEPS", () => {
  it("is non-empty with unique targets", () => {
    expect(TOUR_STEPS.length).toBeGreaterThan(0);
    const targets = TOUR_STEPS.map((s) => s.target);
    expect(new Set(targets).size).toBe(targets.length);
    for (const s of TOUR_STEPS) {
      expect(s.target).toBeTruthy();
    }
  });

  // This pins both identity AND order. Order is not load-bearing for the anchors themselves - each target
  // still has to render somewhere in the workspace, which is what Workspace.test.tsx's "renders a region for
  // every tour step" test proves by iterating this list rather than repeating the five strings - it does not
  // care what order they come in. The order here is the tour's deliberate narrative sequence (capture the
  // meeting, find it in the list, open it, chat about it, then the account menu). A legitimate new step is
  // expected to update this list; that is the point of pinning it rather than just checking membership.
  it("scripts the tour in its narrative order (capture, recordings, detail, chat, account)", () => {
    expect(TOUR_STEPS.map((s) => s.target)).toEqual([
      "capture",
      "recordings",
      "detail",
      "chat",
      "account",
    ]);
  });
});
