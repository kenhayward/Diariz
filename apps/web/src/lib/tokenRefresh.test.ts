import { describe, it, expect } from "vitest";
import { refreshDelayMs, refreshRetryDelayMs, REFRESH_RETRY_DELAYS_MS } from "./tokenRefresh";

// Build a JWT-shaped string with the given exp (seconds). Only the payload matters here.
function tokenWithExp(expSeconds: number): string {
  const payload = btoa(JSON.stringify({ exp: expSeconds })).replace(/\+/g, "-").replace(/\//g, "_");
  return `header.${payload}.sig`;
}

describe("refreshDelayMs", () => {
  it("schedules a refresh one minute before expiry by default", () => {
    const now = 1_000_000_000_000; // ms
    const exp = now / 1000 + 600; // expires in 10 minutes
    expect(refreshDelayMs(tokenWithExp(exp), now)).toBe((600 - 60) * 1000);
  });

  it("returns 0 when already within the skew window", () => {
    const now = 1_000_000_000_000;
    const exp = now / 1000 + 30; // 30s left, under the 60s skew
    expect(refreshDelayMs(tokenWithExp(exp), now)).toBe(0);
  });

  it("returns 0 when already expired", () => {
    const now = 1_000_000_000_000;
    expect(refreshDelayMs(tokenWithExp(now / 1000 - 100), now)).toBe(0);
  });

  it("returns null when there is no token or no exp", () => {
    expect(refreshDelayMs(null, 0)).toBeNull();
    const noExp = `header.${btoa(JSON.stringify({ sub: "x" }))}.sig`;
    expect(refreshDelayMs(noExp, 0)).toBeNull();
  });

  it("honours a custom skew", () => {
    const now = 0;
    expect(refreshDelayMs(tokenWithExp(300), now, 120_000)).toBe((300 - 120) * 1000);
  });
});

// A failed refresh used to be the end of it: rescheduling only happened as a side effect of a SUCCESSFUL
// refresh updating the token, so one failure (an API redeploy, a dropped connection) inside the 60s
// pre-expiry window left the session to lapse - and Stop then landed on a 401, which redirects to /login
// and unmounts the recorder mid-meeting. These delays are what let it try again.
describe("refreshRetryDelayMs", () => {
  it("backs off over the first attempts", () => {
    expect(refreshRetryDelayMs(0)).toBe(REFRESH_RETRY_DELAYS_MS[0]);
    expect(refreshRetryDelayMs(1)).toBe(REFRESH_RETRY_DELAYS_MS[1]);
    expect(refreshRetryDelayMs(2)).toBe(REFRESH_RETRY_DELAYS_MS[2]);
  });

  it("keeps trying at the final interval rather than giving up", () => {
    // The token outlives the retry ladder (120 minutes vs a few minutes of retries), so stopping early
    // would strand a session that a slightly longer outage would have saved.
    const last = REFRESH_RETRY_DELAYS_MS[REFRESH_RETRY_DELAYS_MS.length - 1];
    expect(refreshRetryDelayMs(REFRESH_RETRY_DELAYS_MS.length)).toBe(last);
    expect(refreshRetryDelayMs(99)).toBe(last);
  });

  it("rises, so a long outage is not hammered", () => {
    for (let i = 1; i < REFRESH_RETRY_DELAYS_MS.length; i++) {
      expect(REFRESH_RETRY_DELAYS_MS[i]).toBeGreaterThan(REFRESH_RETRY_DELAYS_MS[i - 1]);
    }
  });
});
