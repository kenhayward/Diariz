import { describe, expect, it } from "vitest";
import { edgeScrollVelocity } from "./dragAutoScroll";

// A 400px-tall viewport from y=100 to y=500.
const rect = { top: 100, bottom: 500 } as DOMRect;
const opts = { threshold: 50, maxSpeed: 20 };

describe("edgeScrollVelocity", () => {
  it("is zero in the dead zone away from both edges", () => {
    expect(edgeScrollVelocity(rect, 300, opts)).toBe(0);
    // Just inside the dead zone on each side (threshold boundary is exclusive of motion).
    expect(edgeScrollVelocity(rect, 150, opts)).toBe(0); // top + threshold
    expect(edgeScrollVelocity(rect, 450, opts)).toBe(0); // bottom - threshold
  });

  it("scrolls up (negative) within the top threshold, ramping to max at the edge", () => {
    expect(edgeScrollVelocity(rect, 100, opts)).toBe(-20); // exactly at the top edge → full speed
    expect(edgeScrollVelocity(rect, 125, opts)).toBe(-10); // halfway through the top zone → half speed
  });

  it("scrolls down (positive) within the bottom threshold, ramping to max at the edge", () => {
    expect(edgeScrollVelocity(rect, 500, opts)).toBe(20); // exactly at the bottom edge → full speed
    expect(edgeScrollVelocity(rect, 475, opts)).toBe(10); // halfway through the bottom zone → half speed
  });

  it("clamps to full speed when the pointer is past an edge (outside the container)", () => {
    expect(edgeScrollVelocity(rect, 50, opts)).toBe(-20); // above the top
    expect(edgeScrollVelocity(rect, 600, opts)).toBe(20); // below the bottom
  });

  it("uses the nearer edge when the container is shorter than two thresholds", () => {
    const tiny = { top: 0, bottom: 60 } as DOMRect; // 60px tall, threshold 50 overlaps
    expect(edgeScrollVelocity(tiny, 10, opts)).toBeLessThan(0); // nearer the top → scroll up
    expect(edgeScrollVelocity(tiny, 50, opts)).toBeGreaterThan(0); // nearer the bottom → scroll down
  });

  it("applies default threshold/maxSpeed when no options are given", () => {
    // Defaults: threshold 48, maxSpeed 18. At the top edge → full negative speed.
    expect(edgeScrollVelocity(rect, 100)).toBe(-18);
    expect(edgeScrollVelocity(rect, 300)).toBe(0);
  });
});
