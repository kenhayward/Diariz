import { describe, it, expect } from "vitest";
import { refreshDelayMs } from "./tokenRefresh";

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
