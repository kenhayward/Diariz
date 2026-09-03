"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const {
  notesWindowBounds,
  compactBounds,
  restoredBounds,
  COMPACT_HEIGHT,
  DEFAULT_SIZE,
  MIN_NOTES_SIZE,
} = require("./notesWindowState");

const laptop = { bounds: { x: 0, y: 0, width: 1536, height: 864 } };
const second = { bounds: { x: 1536, y: 0, width: 1920, height: 1080 } };

test("with nothing remembered, uses the default size and lets the OS place it", () => {
  assert.deepEqual(notesWindowBounds(undefined, [laptop]), { ...DEFAULT_SIZE });
});

test("remembered bounds on an attached display are reused", () => {
  const saved = { x: 200, y: 140, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), saved);
});

test("remembered bounds on a display that is gone fall back to the default", () => {
  const saved = { x: 2000, y: 300, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [laptop]), { ...DEFAULT_SIZE });
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
  assert.deepEqual(notesWindowBounds({ x: "left", y: null }, [laptop]), { ...DEFAULT_SIZE });
  assert.deepEqual(notesWindowBounds(null, [laptop]), { ...DEFAULT_SIZE });
  assert.deepEqual(notesWindowBounds({ x: NaN, y: 0, width: 400, height: 500 }, [laptop]), {
    ...DEFAULT_SIZE,
  });
});

test("no displays at all still yields usable bounds", () => {
  assert.deepEqual(notesWindowBounds({ x: 10, y: 10, width: 400, height: 500 }, []), {
    ...DEFAULT_SIZE,
  });
  assert.deepEqual(notesWindowBounds({ x: 10, y: 10, width: 400, height: 500 }, undefined), {
    ...DEFAULT_SIZE,
  });
});

test("a display entry with no bounds is ignored rather than throwing", () => {
  const saved = { x: 200, y: 140, width: 420, height: 600 };
  assert.deepEqual(notesWindowBounds(saved, [{}, laptop]), saved);
});

test("the default is big enough to show a meeting's worth of stream", () => {
  // The handoff asks for 420x740. That is the DEFAULT, not the minimum: 740 does not fit a 768-tall
  // laptop display once the taskbar has taken its share, and a window that cannot be made to fit is
  // worse than a small one.
  assert.deepEqual(DEFAULT_SIZE, { width: 420, height: 740 });
});

test("the minimum still fits a laptop display with a taskbar", () => {
  assert.deepEqual(MIN_NOTES_SIZE, { width: 360, height: 480 });
});

test("compact keeps where the window is and how wide it is, and only loses the stream", () => {
  // Compact is for a call that has taken the screen: the window shrinks to the composer band so it can
  // sit somewhere out of the way. Moving or resizing it sideways at the same time would lose the place
  // the user had put it.
  const current = { x: 240, y: 90, width: 420, height: 740 };

  assert.deepEqual(compactBounds(current), { x: 240, y: 90, width: 420, height: COMPACT_HEIGHT });
});

test("restoring returns the height the window had, wherever the user has since dragged it", () => {
  // The window can be moved while compact, so the restore keeps the CURRENT position and width and puts
  // back only the height. Restoring the whole saved rectangle would teleport it.
  const current = { x: 900, y: 40, width: 420, height: COMPACT_HEIGHT };

  assert.deepEqual(restoredBounds(current, 740), { x: 900, y: 40, width: 420, height: 740 });
});

test("restoring to a height below the minimum still yields a usable window", () => {
  const current = { x: 10, y: 10, width: 400, height: COMPACT_HEIGHT };

  assert.equal(restoredBounds(current, 100).height, MIN_NOTES_SIZE.height);
});

test("restoring with nothing remembered falls back to the default height", () => {
  const current = { x: 10, y: 10, width: 400, height: COMPACT_HEIGHT };

  assert.equal(restoredBounds(current, undefined).height, DEFAULT_SIZE.height);
  assert.equal(restoredBounds(current, null).height, DEFAULT_SIZE.height);
});

test("a compact height that reached the store anyway is raised on the next open", () => {
  // Belt and braces behind main.js skipping bounds tracking while compact. If a compact height ever did
  // get written - a crash mid-compact, a future edit that forgets the skip - the window would reopen as
  // a 132px sliver with no stream in it and no obvious way back.
  const saved = { x: 100, y: 100, width: 420, height: COMPACT_HEIGHT };

  assert.equal(notesWindowBounds(saved, [laptop]).height, MIN_NOTES_SIZE.height);
});
