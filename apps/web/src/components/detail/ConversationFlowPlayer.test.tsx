import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import ConversationFlowPlayer from "./ConversationFlowPlayer";
import type { SegmentDto } from "../../lib/types";

const seg = (id: string, speaker: string, startMs: number, endMs: number): SegmentDto => ({
  id,
  speaker,
  speakerDisplay: speaker,
  startMs,
  endMs,
  original: "hello",
  revised: null,
  text: "hello",
});

// Ken speaks the first three quarters, Marie the last quarter.
const segments = [seg("s1", "SPEAKER_00", 0, 30_000), seg("s2", "SPEAKER_01", 30_000, 40_000)];
const DURATION = 40_000;

const names: Record<string, string> = { SPEAKER_00: "Ken", SPEAKER_01: "Marie" };

function renderPlayer(over: Partial<Parameters<typeof ConversationFlowPlayer>[0]> = {}) {
  const props = {
    segments,
    durationMs: DURATION,
    currentMs: 10_000,
    playing: false,
    speakerNameOf: (l: string) => names[l] ?? l,
    showOriginal: false,
    canToggleOriginal: true,
    onToggle: vi.fn(),
    onSeek: vi.fn(),
    onToggleOriginal: vi.fn(),
    ...over,
  };
  render(<ConversationFlowPlayer {...props} />);
  return props;
}

beforeEach(() => {
  // jsdom gives every element a zero-size box, so a click would divide by zero. Pin a real width so the
  // seek maths has something to map the pointer's x onto.
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
    left: 0,
    width: 200,
    top: 0,
    height: 20,
    right: 200,
    bottom: 20,
    x: 0,
    y: 0,
    toJSON: () => ({}),
  } as DOMRect);
});

describe("ConversationFlowPlayer", () => {
  it("shows the elapsed and total time", () => {
    renderPlayer();
    expect(screen.getByText("00:10")).toBeTruthy();
    expect(screen.getByText("00:40")).toBeTruthy();
  });

  it("legends each speaker with their share of the talk time", () => {
    renderPlayer();
    expect(screen.getByText(/Ken 75%/)).toBeTruthy();
    expect(screen.getByText(/Marie 25%/)).toBeTruthy();
  });

  it("plays and pauses", () => {
    const p = renderPlayer();
    fireEvent.click(screen.getByRole("button", { name: "Play all" }));
    expect(p.onToggle).toHaveBeenCalled();
  });

  it("shows a pause control while playing", () => {
    renderPlayer({ playing: true });
    expect(screen.getByRole("button", { name: "Pause" })).toBeTruthy();
  });

  it("seeks to the point on the track that was clicked", () => {
    const p = renderPlayer();
    // Half way along a 200px track of a 40s recording => 20s.
    fireEvent.pointerDown(screen.getByRole("slider"), { clientX: 100, pointerId: 1 });
    expect(p.onSeek).toHaveBeenCalledWith(20_000);
  });

  it("reports its position for screen readers", () => {
    renderPlayer();
    const track = screen.getByRole("slider");
    expect(track.getAttribute("aria-valuenow")).toBe("10000");
    expect(track.getAttribute("aria-valuemax")).toBe("40000");
  });

  it("seeks with the arrow keys, so the track is usable without a pointer", () => {
    const p = renderPlayer();
    fireEvent.keyDown(screen.getByRole("slider"), { key: "ArrowRight" });
    expect(p.onSeek).toHaveBeenCalledWith(15_000);
  });

  it("toggles between the model's original words and the revised text", () => {
    const p = renderPlayer();
    fireEvent.click(screen.getByRole("button", { name: "Show original" }));
    expect(p.onToggleOriginal).toHaveBeenCalled();
  });

  it("offers to switch back once the original is showing", () => {
    renderPlayer({ showOriginal: true });
    expect(screen.getByRole("button", { name: "Show revised" })).toBeTruthy();
  });

  it("hides the original/revised toggle when nothing has been revised", () => {
    renderPlayer({ canToggleOriginal: false });
    expect(screen.queryByRole("button", { name: /Show original/ })).toBeNull();
  });
});
