import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useElapsedSeconds } from "./elapsedSeconds";

describe("useElapsedSeconds", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-08-21T10:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("reads zero while inactive, and stays there as time passes", () => {
    const { result } = renderHook(() => useElapsedSeconds(false));
    expect(result.current).toBe(0);
    act(() => void vi.advanceTimersByTime(5000));
    expect(result.current).toBe(0);
  });

  it("starts at zero the moment it becomes active", () => {
    const { result } = renderHook(() => useElapsedSeconds(true));
    expect(result.current).toBe(0);
  });

  it("counts up in whole seconds while active", () => {
    const { result } = renderHook(() => useElapsedSeconds(true));
    act(() => void vi.advanceTimersByTime(1000));
    expect(result.current).toBe(1);
    act(() => void vi.advanceTimersByTime(1000));
    expect(result.current).toBe(2);
    act(() => void vi.advanceTimersByTime(8000));
    expect(result.current).toBe(10);
  });

  // Elapsed comes from the wall clock rather than a tick count, so a throttled background tab that
  // skips ticks still reports the real elapsed time rather than a short one.
  it("reports wall-clock elapsed even when ticks are missed", () => {
    const { result, rerender } = renderHook(({ on }) => useElapsedSeconds(on), {
      initialProps: { on: true },
    });
    act(() => {
      vi.setSystemTime(new Date("2026-08-21T10:00:07Z"));
      vi.advanceTimersByTime(1000);
    });
    rerender({ on: true });
    // >= rather than == because advancing a jumped-forward fake clock lands on the pending tick's own due
    // time (8s), not exactly on the jump. A tick-counting implementation would report 1 here.
    expect(result.current).toBeGreaterThanOrEqual(7);
  });

  it("resets to zero when it goes inactive, and restarts from zero next time", () => {
    const { result, rerender } = renderHook(({ on }) => useElapsedSeconds(on), {
      initialProps: { on: true },
    });
    act(() => void vi.advanceTimersByTime(3000));
    expect(result.current).toBe(3);

    rerender({ on: false });
    expect(result.current).toBe(0);

    rerender({ on: true });
    expect(result.current).toBe(0);
    act(() => void vi.advanceTimersByTime(1000));
    expect(result.current).toBe(1);
  });

  it("stops ticking once unmounted", () => {
    const { unmount } = renderHook(() => useElapsedSeconds(true));
    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });
});
