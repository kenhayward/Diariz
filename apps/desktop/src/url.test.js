"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { normalizeServerUrl, opensExternally } = require("./url");

test("defaults a bare host to https and returns the origin", () => {
  assert.equal(normalizeServerUrl("diariz.example.com"), "https://diariz.example.com");
});

test("keeps an explicit scheme", () => {
  assert.equal(normalizeServerUrl("http://localhost:8081"), "http://localhost:8081");
});

test("strips any path / query / hash", () => {
  assert.equal(normalizeServerUrl("https://diariz.example.com/recordings/1?x=2#y"), "https://diariz.example.com");
});

test("preserves a non-default port", () => {
  assert.equal(normalizeServerUrl("https://diariz.example.com:8443"), "https://diariz.example.com:8443");
});

test("trims surrounding whitespace", () => {
  assert.equal(normalizeServerUrl("  diariz.example.com  "), "https://diariz.example.com");
});

test("rejects empty / blank input", () => {
  assert.equal(normalizeServerUrl(""), null);
  assert.equal(normalizeServerUrl("   "), null);
  assert.equal(normalizeServerUrl(null), null);
});

test("rejects non-http(s) schemes", () => {
  assert.equal(normalizeServerUrl("ftp://example.com"), null);
  assert.equal(normalizeServerUrl("file:///etc/passwd"), null);
});

const ORIGIN = "https://diariz.example.com";

test("a same-origin page opens in one of our own windows, not the browser", () => {
  // The whole point: these are app pages behind the login, so the system browser shows a sign-in prompt.
  assert.equal(opensExternally(`${ORIGIN}/help/recording`, ORIGIN), false);
  assert.equal(opensExternally(`${ORIGIN}/release-notes`, ORIGIN), false);
  assert.equal(opensExternally(`${ORIGIN}/`, ORIGIN), false);
});

test("another site opens in the system browser", () => {
  assert.equal(opensExternally("https://github.com/kenhayward/Diariz", ORIGIN), true);
});

test("origin is host AND scheme AND port, not just hostname", () => {
  assert.equal(opensExternally("http://diariz.example.com/help", ORIGIN), true);
  assert.equal(opensExternally("https://diariz.example.com:8443/help", ORIGIN), true);
  assert.equal(opensExternally("https://evil.diariz.example.com/help", ORIGIN), true);
});

test("non-http schemes belong to the OS", () => {
  assert.equal(opensExternally("mailto:someone@example.com", ORIGIN), true);
  assert.equal(opensExternally("tel:+441234567890", ORIGIN), true);
  assert.equal(opensExternally("about:blank", ORIGIN), true);
});

test("an unparseable target stays external, as it was before", () => {
  assert.equal(opensExternally("not a url", ORIGIN), true);
  assert.equal(opensExternally("", ORIGIN), true);
});

