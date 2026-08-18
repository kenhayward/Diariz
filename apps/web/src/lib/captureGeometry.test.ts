import { describe, expect, it } from "vitest";
import { sourceRectFor } from "./captureGeometry";

const display = { displayWidth: 1920, displayHeight: 1200 };

describe("sourceRectFor", () => {
  it("uses the whole frame when no area was cropped out", () => {
    const rect = sourceRectFor({ width: 1920, height: 1200 }, { ...display, crop: null });

    expect(rect).toEqual({ x: 0, y: 0, width: 1920, height: 1200 });
  });

  it("passes a crop through unchanged when the stream matches the display", () => {
    const crop = { x: 100, y: 50, width: 800, height: 600 };

    expect(sourceRectFor({ width: 1920, height: 1200 }, { ...display, crop })).toEqual(crop);
  });

  // getDisplayMedia treats the requested size as a hint. Chromium commonly returns something smaller,
  // and a crop expressed in the display's physical pixels would then address the wrong region entirely -
  // the capture would look plausible and be of the wrong part of the screen.
  it("rescales a crop when the stream came back smaller than the display", () => {
    const crop = { x: 100, y: 50, width: 800, height: 600 };

    const rect = sourceRectFor({ width: 960, height: 600 }, { ...display, crop });

    expect(rect).toEqual({ x: 50, y: 25, width: 400, height: 300 });
  });

  it("rescales a crop when the stream came back larger than the display", () => {
    const crop = { x: 100, y: 50, width: 800, height: 600 };

    const rect = sourceRectFor({ width: 3840, height: 2400 }, { ...display, crop });

    expect(rect).toEqual({ x: 200, y: 100, width: 1600, height: 1200 });
  });

  it("clamps a crop that runs past the frame rather than asking for pixels that do not exist", () => {
    const crop = { x: 1800, y: 1100, width: 400, height: 400 };

    const rect = sourceRectFor({ width: 1920, height: 1200 }, { ...display, crop });

    expect(rect).toEqual({ x: 1800, y: 1100, width: 120, height: 100 });
  });

  it("falls back to the whole frame for a degenerate crop, which would otherwise draw nothing", () => {
    const crop = { x: 2000, y: 50, width: 400, height: 300 };

    const rect = sourceRectFor({ width: 1920, height: 1200 }, { ...display, crop });

    expect(rect).toEqual({ x: 0, y: 0, width: 1920, height: 1200 });
  });

  it("falls back to the whole frame when the display size is unknown, so no ratio can be formed", () => {
    const crop = { x: 100, y: 50, width: 800, height: 600 };

    const rect = sourceRectFor({ width: 1920, height: 1200 }, { displayWidth: 0, displayHeight: 0, crop });

    expect(rect).toEqual({ x: 0, y: 0, width: 1920, height: 1200 });
  });
});
