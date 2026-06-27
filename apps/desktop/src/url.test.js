"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { normalizeServerUrl } = require("./url");

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
