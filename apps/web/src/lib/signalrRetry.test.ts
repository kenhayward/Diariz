import { describe, it, expect } from "vitest";
import { reconnectDelayMs, RECONNECT_MAX_DELAY_MS } from "./signalrRetry";

// The library default for `withAutomaticReconnect()` with no arguments is [0, 2s, 10s, 30s] and then it
// stops permanently. That is under a minute of trying - shorter than an API container restart - so a
// redeploy left the hub dead until the page was reloaded, and live status updates silently stopped
// arriving for the rest of the session.
describe("reconnectDelayMs", () => {
  it("tries immediately on the first attempt", () => {
    expect(reconnectDelayMs(0)).toBe(0);
  });

  it("backs off over the early attempts", () => {
    const early = [0, 1, 2, 3, 4].map(reconnectDelayMs);
    for (let i = 1; i < early.length; i++) {
      expect(early[i]).toBeGreaterThan(early[i - 1]);
    }
  });

  it("keeps retrying indefinitely instead of giving up", () => {
    // The whole point: a hub that stopped trying is indistinguishable to the user from an app that has
    // quietly stopped working. Retrying a dead server once a minute costs nothing.
    expect(reconnectDelayMs(50)).not.toBeNull();
    expect(reconnectDelayMs(10_000)).not.toBeNull();
  });

  it("holds at a ceiling so a long outage is not hammered", () => {
    expect(reconnectDelayMs(50)).toBe(RECONNECT_MAX_DELAY_MS);
    expect(reconnectDelayMs(10_000)).toBe(RECONNECT_MAX_DELAY_MS);
  });

  it("covers a container restart within the first few attempts", () => {
    // An API restart runs migrations and seeders before it listens; the compose healthcheck allows 60s.
    // The client must still be trying well past that point.
    const elapsed = [0, 1, 2, 3, 4, 5].reduce((sum, i) => sum + reconnectDelayMs(i), 0);
    expect(elapsed).toBeGreaterThan(60_000);
  });
});
