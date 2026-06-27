"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const {
  formatElapsed,
  trayRecorderItems,
  trayTooltip,
  notificationFor,
} = require("./recorderState");

test("formatElapsed renders mm:ss, zero-padded, clamped at zero", () => {
  assert.equal(formatElapsed(0), "00:00");
  assert.equal(formatElapsed(5_000), "00:05");
  assert.equal(formatElapsed(65_000), "01:05");
  assert.equal(formatElapsed(600_000), "10:00");
  assert.equal(formatElapsed(-1_000), "00:00");
});

test("idle + ready shows both record items, enabled", () => {
  const items = trayRecorderItems({ phase: "idle", ready: true }, 0);
  assert.deepEqual(
    items.map((i) => [i.id, i.label, i.enabled]),
    [
      ["record-mic", "Record Microphone", true],
      ["record-system", "Record System Audio", true],
    ],
  );
});

test("idle but not ready disables the record items", () => {
  const items = trayRecorderItems({ phase: "idle", ready: false }, 0);
  assert.equal(items.length, 2);
  assert.ok(items.every((i) => i.enabled === false));
});

test("recording shows a single Stop item with the live elapsed time", () => {
  const items = trayRecorderItems({ phase: "recording", source: "mic", ready: true }, 65_000);
  assert.equal(items.length, 1);
  assert.equal(items[0].id, "stop");
  assert.equal(items[0].label, "Stop Recording (01:05)");
  assert.equal(items[0].enabled, true);
});

test("uploading shows a disabled progress item", () => {
  const items = trayRecorderItems({ phase: "uploading", ready: true }, 0);
  assert.equal(items.length, 1);
  assert.equal(items[0].id, "uploading");
  assert.equal(items[0].enabled, false);
});

test("tooltip reflects the phase and source", () => {
  assert.equal(trayTooltip({ phase: "idle", ready: true }), "Diariz");
  assert.equal(trayTooltip({ phase: "recording", source: "system", ready: true }), "Diariz — recording system audio");
  assert.equal(trayTooltip({ phase: "recording", source: "mic", ready: true }), "Diariz — recording microphone");
  assert.equal(trayTooltip({ phase: "uploading", ready: true }), "Diariz — uploading…");
});

test("notificationFor fires on start, on uploaded, and on error only", () => {
  // start (any non-recording -> recording)
  assert.deepEqual(notificationFor({ phase: "idle" }, { phase: "recording", source: "mic" }), {
    title: "Diariz",
    body: "Recording microphone…",
  });
  assert.deepEqual(notificationFor({ phase: "idle" }, { phase: "recording", source: "system" }), {
    title: "Diariz",
    body: "Recording system audio…",
  });
  // upload finished
  assert.deepEqual(notificationFor({ phase: "uploading" }, { phase: "idle" }), {
    title: "Diariz",
    body: "Recording uploaded",
  });
  // error
  assert.deepEqual(notificationFor({ phase: "recording" }, { phase: "error", error: "Mic in use" }), {
    title: "Diariz",
    body: "Mic in use",
  });
  // no notification for intermediate / unrelated transitions
  assert.equal(notificationFor({ phase: "recording" }, { phase: "uploading" }), null);
  assert.equal(notificationFor({ phase: "recording", source: "mic" }, { phase: "recording", source: "mic" }), null);
});
