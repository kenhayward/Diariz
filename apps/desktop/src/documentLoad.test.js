"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { documentLoadOptions, trayReloadItem } = require("./documentLoad");

test("the document is always loaded with a no-cache directive", () => {
  const headers = documentLoadOptions().extraHeaders || "";
  // The shell's whole job is loading someone else's SPA over HTTP. Without this the browser cache decides
  // when the app updates, and it decided "not yet" for three releases running.
  assert.match(headers.toLowerCase(), /no-cache/);
  // Header blocks must be newline-terminated or Chromium drops the last one.
  assert.ok(headers.endsWith("\n"), "extraHeaders must end with a newline");
});

test("the no-cache directive covers only the document, not its assets", () => {
  // A guard against someone "fixing" this by clearing the whole session cache: the hashed bundles are
  // immutable and re-downloading megabytes of them on every launch would be a real cost.
  const opts = documentLoadOptions();
  assert.deepStrictEqual(Object.keys(opts), ["extraHeaders"]);
});

test("Reload is offered once a server is configured", () => {
  const item = trayReloadItem("https://diariz.example.com");
  assert.ok(item);
  assert.strictEqual(item.id, "reload");
  assert.match(item.label, /reload/i);
});

test("Reload is absent with no server to reload from", () => {
  // First run, before setup. An item that could only ever open the setup window would be a second, worse
  // route to something the menu already offers.
  assert.strictEqual(trayReloadItem(null), null);
  assert.strictEqual(trayReloadItem(""), null);
  assert.strictEqual(trayReloadItem(undefined), null);
});
