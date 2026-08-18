"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { cropRectFor, clampRect, resizeDims, sourceForDisplay } = require("./captureTarget");

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

// ---- sourceForDisplay -----------------------------------------------------------------------------
//
// desktopCapturer hands back one source per screen, and `display_id` is NOT contractually populated on
// every platform. Falling back to sources[0] would silently capture the wrong monitor - the picture
// would look plausible and be of somewhere else entirely - so a miss has to be a miss.

test("sourceForDisplay picks the source belonging to the display", () => {
  const sources = [
    { id: "screen:0", display_id: "111" },
    { id: "screen:1", display_id: "222" },
  ];

  assert.equal(sourceForDisplay(sources, 222).id, "screen:1");
});

test("sourceForDisplay compares ids as strings, since the two sides disagree on type", () => {
  // screen.getAllDisplays() gives a number; desktopCapturer gives a string.
  assert.equal(sourceForDisplay([{ id: "screen:0", display_id: "222" }], 222).id, "screen:0");
});

test("sourceForDisplay returns null rather than guessing when nothing matches", () => {
  const sources = [
    { id: "screen:0", display_id: "111" },
    { id: "screen:1", display_id: "222" },
  ];

  assert.equal(sourceForDisplay(sources, 999), null);
});

test("sourceForDisplay returns null when the platform populated no display ids at all", () => {
  assert.equal(sourceForDisplay([{ id: "screen:0", display_id: "" }], 222), null);
});

test("sourceForDisplay handles an empty source list", () => {
  assert.equal(sourceForDisplay([], 222), null);
  assert.equal(sourceForDisplay(undefined, 222), null);
});
