import { describe, it, expect } from "vitest";
import { start, pause, resume, elapsedMs } from "./recorderTiming";

describe("recorderTiming", () => {
  it("counts only running time, excluding paused stretches", () => {
    let t = start(1000);
    expect(elapsedMs(t, 4000)).toBe(3000); // ran 3s

    t = pause(t, 4000); // fold 3s, clock stops
    expect(elapsedMs(t, 14000)).toBe(3000); // 10s paused adds nothing

    t = resume(t, 14000);
    expect(elapsedMs(t, 16000)).toBe(5000); // + 2s running = 5s recorded (not 15s wall-clock)

    t = pause(t, 16000);
    expect(elapsedMs(t, 99999)).toBe(5000);
  });

  it("pause is idempotent and resume from running is a no-op", () => {
    let t = start(0);
    t = pause(t, 1000);
    expect(pause(t, 5000)).toEqual(t); // already paused
    const running = resume(t, 2000);
    expect(resume(running, 9999)).toEqual(running); // already running
  });

  it("never returns negative elapsed for out-of-order clocks", () => {
    const t = start(5000);
    expect(elapsedMs(t, 4000)).toBe(0);
  });
});
