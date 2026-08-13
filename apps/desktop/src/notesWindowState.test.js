"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { notesWindowBounds, MIN_NOTES_SIZE } = require("./notesWindowState");

const laptop = { bounds: { x: 0, y: 0, width: 1536, height: 864 } };
const second = { bounds: { x: 1536, y: 0, width: 1920, height: 1080 } };

test("with nothing remembered, uses the default size and lets the OS place it", () => {
  assert.deepEqual(notesWindowBounds(undefined, [laptop]), { width: 380, height: 520 });
});

test("remembered bounds on an attached display are reused", () => {
  const saved = { x: 200, y: 140, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), saved);
});

test("remembered bounds on a display that is gone fall back to the default", () => {
  const saved = { x: 2000, y: 300, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), { width: 380, height: 520 });
});

test("remembered bounds on a second display are kept while it is attached", () => {
  const saved = { x: 2000, y: 300, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop, second]), saved);
});

test("a remembered size too small to use is raised to the minimum", () => {
  const saved = { x: 100, y: 100, width: 40, height: 30 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), {
    x: 100,
    y: 100,
    width: MIN_NOTES_SIZE.width,
    height: MIN_NOTES_SIZE.height,
  });
});

test("garbage in the store does not propagate", () => {
  assert.deepEqual(notesWindowBounds({ x: "left", y: null }, [laptop]), { width: 380, height: 520 });
  assert.deepEqual(notesWindowBounds(null, [laptop]), { width: 380, height: 520 });
  assert.deepEqual(notesWindowBounds({ x: NaN, y: 0, width: 400, height: 500 }, [laptop]), {
    width: 380,
    height: 520,
  });
});

test("no displays at all still yields usable bounds", () => {
  assert.deepEqual(notesWindowBounds({ x: 10, y: 10, width: 400, height: 500 }, []), {
    width: 380,
    height: 520,
  });
  assert.deepEqual(notesWindowBounds({ x: 10, y: 10, width: 400, height: 500 }, undefined), {
    width: 380,
    height: 520,
  });
});

test("a display entry with no bounds is ignored rather than throwing", () => {
  const saved = { x: 200, y: 140, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [{}, laptop]), saved);
});
