import { getConfig } from "@testing-library/dom";
import { describe, expect, it } from "vitest";

/// What the shared setup actually put in place, asserted rather than assumed.
///
/// `setupFiles` runs per test file and nothing else checks it took effect, so a line silently removed - or
/// a later import resetting the value - would go unnoticed until something flaked.
describe("test setup", () => {
  it("gives findBy/waitFor a budget matched to this suite, not the 1s default", () => {
    // `testTimeout` in vitest.config.ts was raised to 20s specifically because the default flaked under
    // contention. That setting does not govern testing-library's async utilities: `findBy*` and `waitFor`
    // use their own `asyncUtilTimeout`, which defaults to 1000ms - so the fix left the assertions that
    // actually wait on the old budget. Everything here that awaits an element awaits it through those.
    //
    // Kept below `testTimeout` on purpose: a missing element then fails as "unable to find an element",
    // naming what it looked for, instead of an opaque test timeout.
    expect(getConfig().asyncUtilTimeout).toBe(5000);
  });
});
