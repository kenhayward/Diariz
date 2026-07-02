import { render, screen, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { StatusProvider, useStatus } from "./status";

function Harness() {
  const { status, setStatus } = useStatus();
  return (
    <div>
      <span data-testid="msg">{status ? `${status.tone}:${status.text}` : "none"}</span>
      <button onClick={() => setStatus("Working…", "progress")}>progress</button>
      <button onClick={() => setStatus("Done.", "success")}>success</button>
      <button onClick={() => setStatus(null)}>clear</button>
    </div>
  );
}

describe("StatusProvider", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const msg = () => screen.getByTestId("msg").textContent;

  it("shows a pushed message and keeps progress sticky", () => {
    render(<StatusProvider><Harness /></StatusProvider>);
    expect(msg()).toBe("none");
    act(() => screen.getByText("progress").click());
    expect(msg()).toBe("progress:Working…");
    act(() => vi.advanceTimersByTime(10000));
    expect(msg()).toBe("progress:Working…"); // progress does not auto-clear
  });

  it("auto-clears a non-sticky (success) message", () => {
    render(<StatusProvider><Harness /></StatusProvider>);
    act(() => screen.getByText("success").click());
    expect(msg()).toBe("success:Done.");
    act(() => vi.advanceTimersByTime(6000));
    expect(msg()).toBe("none");
  });

  it("clears when set to null", () => {
    render(<StatusProvider><Harness /></StatusProvider>);
    act(() => screen.getByText("progress").click());
    act(() => screen.getByText("clear").click());
    expect(msg()).toBe("none");
  });
});
