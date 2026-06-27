"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { updateRestartItem, notificationForUpdate } = require("./updateState");

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
  assert.deepEqual(notificationForUpdate("not-available", { manual: true }), {
    title: "Diariz",
    body: "You're already up to date",
  });
  assert.deepEqual(notificationForUpdate("error", { manual: true }), {
    title: "Diariz",
    body: "Update check failed",
  });
});

test("automatic (non-manual) checks stay quiet except when an update is downloaded", () => {
  assert.equal(notificationForUpdate("available", { version: "0.12.0" }), null);
  assert.equal(notificationForUpdate("not-available", {}), null);
  assert.equal(notificationForUpdate("error", {}), null);
});
