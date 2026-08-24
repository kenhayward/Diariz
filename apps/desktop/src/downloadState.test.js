"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { notificationForDownload } = require("./downloadState");

test("a long download that completed announces where it went", () => {
  assert.deepEqual(
    notificationForDownload("completed", { filename: "diariz-backup.zip", elapsedMs: 30_000 }),
    { title: "Diariz", body: "Saved diariz-backup.zip" },
  );
});

test("a quick download stays silent", () => {
  // The handler covers every download in the app. A 5 KB transcript finishing does not need an OS
  // notification - you are still looking at the window that started it.
  assert.equal(
    notificationForDownload("completed", { filename: "transcript.md", elapsedMs: 1200 }),
    null,
  );
});

test("a failed download always announces itself, however quick", () => {
  // Failures are the case you must not miss, and they tend to fail fast.
  assert.deepEqual(
    notificationForDownload("interrupted", { filename: "diariz-backup.zip", elapsedMs: 200 }),
    { title: "Diariz", body: "Download failed - diariz-backup.zip" },
  );
});

test("a download the user cancelled stays silent", () => {
  assert.equal(
    notificationForDownload("cancelled", { filename: "diariz-backup.zip", elapsedMs: 30_000 }),
    null,
  );
});

test("an unknown state raises nothing", () => {
  assert.equal(notificationForDownload("weird", { filename: "x.zip", elapsedMs: 30_000 }), null);
  assert.equal(notificationForDownload("completed", undefined), null);
});
