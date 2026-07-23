import { describe, it, expect } from "vitest";
import {
  clampScale,
  computeFitScale,
  computeNativeScale,
  computeMaxScale,
  computeDisplaySize,
  stepScale,
  zoomTowardPoint,
  clampPanOffset,
  initialZoomState,
  MIN_SCALE,
} from "./imageZoom";

describe("clampScale", () => {
  it("passes through a value already within bounds", () => {
    expect(clampScale(2, 1, 5)).toBe(2);
  });

  it("clamps below the minimum", () => {
    expect(clampScale(0.4, 1, 5)).toBe(1);
  });

  it("clamps above the maximum", () => {
    expect(clampScale(9, 1, 5)).toBe(5);
  });
});

describe("computeFitScale", () => {
  it("constrains a wide image by viewport width", () => {
    // 3840x2160 in a 960x600 viewport: width ratio 0.25, height ratio ~0.278 -> width wins
    expect(computeFitScale(3840, 2160, 960, 600)).toBeCloseTo(0.25, 5);
  });

  it("constrains a tall image by viewport height", () => {
    // 1000x4000 in a 800x600 viewport: width ratio 0.8, height ratio 0.15 -> height wins
    expect(computeFitScale(1000, 4000, 800, 600)).toBeCloseTo(0.15, 5);
  });

  it("never upscales an image already smaller than the viewport", () => {
    expect(computeFitScale(400, 300, 960, 600)).toBe(1);
  });

  it("falls back to 1 when viewport dimensions are zero (no jsdom layout)", () => {
    expect(computeFitScale(3840, 2160, 0, 0)).toBe(1);
  });

  it("falls back to 1 when image dimensions are zero", () => {
    expect(computeFitScale(0, 0, 960, 600)).toBe(1);
  });
});

describe("computeNativeScale", () => {
  it("is the reciprocal of the fit scale for a capture larger than the viewport", () => {
    // fit = 0.25 -> native (100%) = 4x fit
    expect(computeNativeScale(3840, 2160, 960, 600)).toBeCloseTo(4, 5);
  });

  it("is 1 (== fit) for a capture already smaller than the viewport", () => {
    expect(computeNativeScale(400, 300, 960, 600)).toBe(1);
  });
});

describe("computeMaxScale", () => {
  it("uses the default 5x-fit cap when native scale is smaller than the cap", () => {
    // native = 4x here (see above), less than the default cap of 5
    expect(computeMaxScale(3840, 2160, 960, 600)).toBe(5);
  });

  it("uses native scale when it exceeds the cap, so a dense capture can reach true resolution", () => {
    // fit = min(1, 500/6000, 500/4000) = 0.08333... -> native = 12x, above the 5x cap
    expect(computeMaxScale(6000, 4000, 500, 500)).toBeCloseTo(12, 5);
  });

  it("respects a custom cap multiple", () => {
    expect(computeMaxScale(3840, 2160, 960, 600, 3)).toBeCloseTo(4, 5); // native (4) still wins over cap 3
    expect(computeMaxScale(3840, 2160, 960, 600, 10)).toBe(10); // cap (10) wins over native (4)
  });
});

describe("computeDisplaySize", () => {
  it("scales the image dimensions by fitScale * scale", () => {
    expect(computeDisplaySize(1000, 500, 0.5, 2)).toEqual({ width: 1000, height: 500 });
  });
});

describe("stepScale", () => {
  it("zooms in by the default factor", () => {
    expect(stepScale(1, 1, 1, 5)).toBeCloseTo(1.25, 5);
  });

  it("zooms out by the default factor", () => {
    expect(stepScale(2, -1, 1, 5)).toBeCloseTo(1.6, 5);
  });

  it("clamps zooming out below the minimum", () => {
    expect(stepScale(1, -1, 1, 5)).toBe(1);
  });

  it("clamps zooming in above the maximum", () => {
    expect(stepScale(4.5, 1, 1, 5)).toBe(5);
  });

  it("accepts a custom step factor", () => {
    expect(stepScale(1, 1, 1, 10, 2)).toBe(2);
  });
});

describe("zoomTowardPoint", () => {
  it("keeps the content point under the cursor fixed on screen when zooming in from no offset", () => {
    const cursor = { x: 50, y: 30 };
    const next = zoomTowardPoint({ x: 0, y: 0 }, 1, 2, cursor);
    expect(next).toEqual({ x: -50, y: -30 });

    // Verify: content point under the cursor before == content point under the cursor after.
    const contentBefore = { x: (cursor.x - 0) / 1, y: (cursor.y - 0) / 1 };
    const screenAfter = { x: next.x + contentBefore.x * 2, y: next.y + contentBefore.y * 2 };
    expect(screenAfter).toEqual(cursor);
  });

  it("keeps the content point under the cursor fixed when already panned", () => {
    const offset = { x: 20, y: -15 };
    const cursor = { x: 10, y: 40 };
    const next = zoomTowardPoint(offset, 2, 3, cursor);

    const contentBefore = { x: (cursor.x - offset.x) / 2, y: (cursor.y - offset.y) / 2 };
    const screenAfter = { x: next.x + contentBefore.x * 3, y: next.y + contentBefore.y * 3 };
    expect(screenAfter.x).toBeCloseTo(cursor.x, 10);
    expect(screenAfter.y).toBeCloseTo(cursor.y, 10);
  });

  it("zooming toward the center (0,0) scales an existing offset proportionally", () => {
    const next = zoomTowardPoint({ x: 40, y: -20 }, 1, 2, { x: 0, y: 0 });
    expect(next).toEqual({ x: 80, y: -40 });
  });
});

describe("clampPanOffset", () => {
  it("forces the offset to zero on an axis where the image already fits the viewport", () => {
    expect(clampPanOffset({ x: 100, y: 100 }, 400, 300, 960, 600)).toEqual({ x: 0, y: 0 });
  });

  it("passes through an offset within the allowed drag range", () => {
    // displayed 1200x800 in an 800x600 viewport -> max |offset| = (1200-800)/2=200, (800-600)/2=100
    expect(clampPanOffset({ x: 50, y: -30 }, 1200, 800, 800, 600)).toEqual({ x: 50, y: -30 });
  });

  it("clamps an offset that would drag the image fully off the viewport", () => {
    expect(clampPanOffset({ x: 500, y: -500 }, 1200, 800, 800, 600)).toEqual({ x: 200, y: -100 });
  });

  it("clamps independently per axis", () => {
    // x overflows (displayed 1200 > viewport 800) but y fits (displayed 500 <= viewport 600)
    expect(clampPanOffset({ x: 999, y: 999 }, 1200, 500, 800, 600)).toEqual({ x: 200, y: 0 });
  });
});

describe("initialZoomState", () => {
  it("returns fit scale (1) and zero offset", () => {
    expect(initialZoomState()).toEqual({ scale: MIN_SCALE, offset: { x: 0, y: 0 } });
  });
});
