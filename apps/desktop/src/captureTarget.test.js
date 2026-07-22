"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { cropRectFor, clampRect, resizeDims } = require("./captureTarget");

const display = { id: 1, bounds: { x: 0, y: 0, width: 1920, height: 1080 }, scaleFactor: 1 };
const hidpi = { id: 2, bounds: { x: 0, y: 0, width: 1920, height: 1080 }, scaleFactor: 2 };

test("a whole-screen selection needs no crop", () => {
  assert.equal(cropRectFor(display, { kind: "screen", displayId: 1 }), null);
});

test("a region selection maps DIP to physical pixels at scale 1", () => {
  const rect = cropRectFor(display, { kind: "region", displayId: 1, rect: { x: 10, y: 20, width: 300, height: 200 } });
  assert.deepEqual(rect, { x: 10, y: 20, width: 300, height: 200 });
});

test("a region selection scales by the display scale factor", () => {
  const rect = cropRectFor(hidpi, { kind: "region", displayId: 2, rect: { x: 10, y: 20, width: 300, height: 200 } });
  assert.deepEqual(rect, { x: 20, y: 40, width: 600, height: 400 });
});

test("a region selection is clamped to the display", () => {
  const rect = cropRectFor(display, { kind: "region", displayId: 1, rect: { x: 1800, y: 1000, width: 400, height: 400 } });
  assert.deepEqual(rect, { x: 1800, y: 1000, width: 120, height: 80 });
});

test("clampRect pulls negative origins back to zero without growing the rect past the bounds", () => {
  assert.deepEqual(clampRect({ x: -50, y: -10, width: 200, height: 100 }, { width: 800, height: 600 }), {
    x: 0, y: 0, width: 150, height: 90,
  });
});

test("resizeDims leaves an already-small image alone", () => {
  assert.deepEqual(resizeDims(800, 600, 2560), { width: 800, height: 600 });
});

test("resizeDims scales a wide image down by its long edge", () => {
  assert.deepEqual(resizeDims(3840, 2160, 2560), { width: 2560, height: 1440 });
});

test("resizeDims scales a tall image down by its long edge", () => {
  assert.deepEqual(resizeDims(1000, 4000, 2000), { width: 500, height: 2000 });
});
