"use strict";

// Regression guard for the packaged-build crash "TypeError: Store is not a
// constructor" (main.js `new Store(...)`). electron-store went pure ESM at v9,
// so `require("electron-store")` in this CommonJS main process returns a module
// namespace object instead of the constructor. Keep the dependency on the last
// CJS line (v8.x) until main.js is converted to ESM / dynamic import.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

test("electron-store resolves to a constructor (require works in CommonJS main)", () => {
  const Store = require("electron-store");
  assert.equal(typeof Store, "function", "electron-store must be require-able as a constructor");
  assert.equal(typeof Store.prototype?.get, "function");
});

test("declared electron-store dependency stays on the CommonJS line (major < 9)", () => {
  const pkg = require(path.join(__dirname, "..", "package.json"));
  const range = pkg.dependencies["electron-store"];
  const major = Number(range.replace(/^[^\d]*/, "").split(".")[0]);
  assert.ok(
    major < 9,
    `electron-store is ESM-only from v9; main.js is CommonJS, so pin < 9 (got "${range}")`,
  );
});
