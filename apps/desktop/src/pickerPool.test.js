"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { reconcilePool } = require("./pickerPool");

test("an empty pool needs a window for every display", () => {
  assert.deepEqual(reconcilePool([], [1, 2, 3]), { create: [1, 2, 3], destroy: [], keep: [] });
});

test("a pool that already matches the displays needs no work", () => {
  assert.deepEqual(reconcilePool([1, 2, 3], [1, 2, 3]), { create: [], destroy: [], keep: [1, 2, 3] });
});

test("a newly plugged-in display gets a window without disturbing the others", () => {
  assert.deepEqual(reconcilePool([1, 2], [1, 2, 3]), { create: [3], destroy: [], keep: [1, 2] });
});

test("an unplugged display's window is destroyed and the rest are kept", () => {
  assert.deepEqual(reconcilePool([1, 2, 3], [1, 3]), { create: [], destroy: [2], keep: [1, 3] });
});

test("a wholesale display swap creates the new set and destroys the old", () => {
  assert.deepEqual(reconcilePool([1, 2], [7, 8]), { create: [7, 8], destroy: [1, 2], keep: [] });
});

test("teardown - no displays - destroys everything the pool holds", () => {
  assert.deepEqual(reconcilePool([1, 2], []), { create: [], destroy: [1, 2], keep: [] });
});

test("display ids are compared as values, not identities, so a repeated id is not duplicated", () => {
  assert.deepEqual(reconcilePool([1, 1, 2], [1, 2]), { create: [], destroy: [], keep: [1, 2] });
});

test("reconciling an empty pool against no displays is a no-op, not a crash", () => {
  assert.deepEqual(reconcilePool([], []), { create: [], destroy: [], keep: [] });
});
