"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { buildStartUrl, codeFromArgv, notificationForAuthError } = require("./desktopAuth");

test("buildStartUrl points at the server's Google start with the desktop challenge", () => {
  const url = buildStartUrl("https://diariz.example.com", "CHALLENGE123");
  assert.equal(url, "https://diariz.example.com/api/auth/google/start?desktopChallenge=CHALLENGE123");
});

test("buildStartUrl trims a trailing slash on the server origin", () => {
  const url = buildStartUrl("https://diariz.example.com/", "abc");
  assert.equal(url, "https://diariz.example.com/api/auth/google/start?desktopChallenge=abc");
});

test("buildStartUrl url-encodes the challenge", () => {
  const url = buildStartUrl("https://x.test", "a b+c");
  assert.equal(url, "https://x.test/api/auth/google/start?desktopChallenge=a%20b%2Bc");
});

test("codeFromArgv extracts the code from a diariz:// deep link anywhere in argv", () => {
  assert.equal(codeFromArgv(["app.exe", "diariz://auth/callback?code=THE_CODE"]), "THE_CODE");
  assert.equal(codeFromArgv(["diariz://auth/callback?code=abc&x=1"]), "abc");
});

test("codeFromArgv url-decodes the code", () => {
  assert.equal(codeFromArgv(["diariz://auth/callback?code=a%2Bb"]), "a+b");
});

test("codeFromArgv returns null for junk / no code / wrong host", () => {
  assert.equal(codeFromArgv(["app.exe", "--flag"]), null);
  assert.equal(codeFromArgv(["diariz://auth/callback"]), null);
  assert.equal(codeFromArgv(["diariz://other/path?code=x"]), null);
  assert.equal(codeFromArgv([]), null);
});

test("codeFromArgv rejects a look-alike path that only starts with the callback prefix", () => {
  assert.equal(codeFromArgv(["diariz://auth/callback-evil?code=x"]), null);
});

test("notificationForAuthError gives a distinct, titled message per failure reason", () => {
  assert.equal(notificationForAuthError("network").title, "Diariz");
  assert.match(notificationForAuthError("network").body, /reach the server/i);
  assert.match(notificationForAuthError("expired").body, /interrupted|sign in again/i);
  assert.match(notificationForAuthError("rejected").body, /didn't complete|try again/i);
  // Unknown reasons fall back to the generic message rather than throwing.
  assert.match(notificationForAuthError("something-else").body, /didn't complete|try again/i);
});
