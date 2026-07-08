"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { updateRestartItem, notificationForUpdate, isNewerVersion } = require("./updateState");

test("updateRestartItem is null until an update is downloaded", () => {
  assert.equal(updateRestartItem({ ready: false }), null);
  assert.equal(updateRestartItem(undefined), null);
});

test("updateRestartItem shows a versioned restart item once ready", () => {
  assert.deepEqual(updateRestartItem({ ready: true, version: "0.12.0" }), {
    id: "restart-update",
    label: "Restart to update (0.12.0)",
  });
});

test("updateRestartItem omits the version when unknown", () => {
  assert.deepEqual(updateRestartItem({ ready: true }), {
    id: "restart-update",
    label: "Restart to update",
  });
});

test("a downloaded update always notifies, with the version when known", () => {
  assert.deepEqual(notificationForUpdate("downloaded", { version: "0.12.0" }), {
    title: "Diariz",
    body: "Update 0.12.0 is ready — restart to update",
  });
  assert.deepEqual(notificationForUpdate("downloaded", {}), {
    title: "Diariz",
    body: "An update is ready — restart to update",
  });
});

test("a manual check gives feedback for available / up-to-date / error", () => {
  assert.deepEqual(notificationForUpdate("available", { version: "0.12.0", manual: true }), {
    title: "Diariz",
    body: "Downloading update 0.12.0…",
  });
  assert.deepEqual(notificationForUpdate("not-available", { manual: true, version: "0.98.0" }), {
    title: "Diariz",
    body: "You are on the latest version 0.98.0",
  });
  assert.deepEqual(notificationForUpdate("error", { manual: true }), {
    title: "Diariz",
    body: "Update check failed",
  });
});

test("the up-to-date notification omits the version when unknown", () => {
  assert.deepEqual(notificationForUpdate("not-available", { manual: true }), {
    title: "Diariz",
    body: "You are on the latest version",
  });
});

test("automatic (non-manual) checks stay quiet except when an update is downloaded", () => {
  assert.equal(notificationForUpdate("available", { version: "0.12.0" }), null);
  assert.equal(notificationForUpdate("not-available", {}), null);
  assert.equal(notificationForUpdate("error", {}), null);
});

// isNewerVersion drives the macOS manual "Check for Updates" (which compares the app version against the
// latest GitHub release tag). Compares Major.Minor.Build numerically, tolerating a leading "v".
test("isNewerVersion is true only when latest is strictly greater", () => {
  assert.equal(isNewerVersion("0.105.3", "0.105.4"), true); // build
  assert.equal(isNewerVersion("0.105.3", "0.106.0"), true); // minor
  assert.equal(isNewerVersion("0.105.3", "1.0.0"), true); // major
  assert.equal(isNewerVersion("0.105.3", "0.105.3"), false); // equal
  assert.equal(isNewerVersion("0.105.3", "0.105.2"), false); // older
  assert.equal(isNewerVersion("0.105.3", "0.104.9"), false);
});

test("isNewerVersion tolerates a leading v and whitespace on the tag", () => {
  assert.equal(isNewerVersion("0.105.3", "v0.105.4"), true);
  assert.equal(isNewerVersion("0.105.3", "  v0.105.4  "), true);
});

test("isNewerVersion compares segments numerically, not lexically", () => {
  assert.equal(isNewerVersion("0.9.9", "0.10.0"), true); // 10 > 9, not "10" < "9"
  assert.equal(isNewerVersion("0.100.0", "0.99.9"), false);
});

test("isNewerVersion returns false for missing or unparseable input (never prompt on bad data)", () => {
  assert.equal(isNewerVersion("0.105.3", ""), false);
  assert.equal(isNewerVersion("0.105.3", null), false);
  assert.equal(isNewerVersion("0.105.3", "not-a-version"), false);
  assert.equal(isNewerVersion("", "0.105.4"), false);
  assert.equal(isNewerVersion(undefined, "0.105.4"), false);
});
