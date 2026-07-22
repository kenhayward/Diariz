"use strict";

const test = require("node:test");
const assert = require("node:assert");
const {
  DEFAULT_ACCELERATOR,
  trayScreenshotItems,
  isValidAccelerator,
  normalizeAccelerator,
} = require("./screenshotState");

test("the default accelerator is a valid one", () => {
  assert.equal(isValidAccelerator(DEFAULT_ACCELERATOR), true);
});

test("no screenshot items exist while idle", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "idle", ready: true }), []);
});

test("no screenshot items exist while uploading", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "uploading" }), []);
});

test("capture and change-area items appear while recording", () => {
  const items = trayScreenshotItems({ phase: "recording", source: "mic" });
  assert.deepEqual(items.map((i) => i.id), ["capture", "change-area"]);
  assert.ok(items.every((i) => i.enabled));
  assert.equal(items[0].label, "Capture Screenshot");
  assert.equal(items[1].label, "Change Capture Area...");
});

test("an accelerator with no modifier is rejected", () => {
  assert.equal(isValidAccelerator("S"), false);
});

test("an accelerator with no key is rejected", () => {
  assert.equal(isValidAccelerator("Control+Shift"), false);
});

test("an empty accelerator is rejected", () => {
  assert.equal(isValidAccelerator("   "), false);
});

test("normalizeAccelerator tidies casing and spacing", () => {
  assert.equal(normalizeAccelerator(" control + shift + p "), "Control+Shift+P");
});

test("normalizeAccelerator returns null for an invalid combination", () => {
  assert.equal(normalizeAccelerator("Shift"), null);
});
