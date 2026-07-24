import { test } from "node:test";
import assert from "node:assert/strict";
import { describeError, joinUrl } from "../nodes/Diariz/transport/request";
import { applyLimit } from "../nodes/Diariz/transport/pagination";

test("explains an expired or revoked token", () => {
  assert.match(describeError(401, {}), /expired or been revoked/i);
});

test("explains a disabled platform capability", () => {
  assert.match(describeError(403, {}), /administrator/i);
});

test("passes a server message through when there is one", () => {
  assert.match(
    describeError(400, "Automation limit reached. Delete one before adding another."),
    /Automation limit reached/,
  );
});

test("prefers a problem-details title over a bare status", () => {
  assert.match(describeError(400, { title: "That key is already in use." }), /already in use/);
});

test("falls back to naming the status code", () => {
  assert.match(describeError(500, {}), /500/);
});

test("uses plain hyphens in every error message", () => {
  const messages = [401, 403, 404, 400, 500].map((s) => describeError(s, {}));
  assert.ok(!/[–—]/.test(messages.join(" ")));
});

test("trims a trailing slash off the base url", () => {
  assert.equal(joinUrl("https://d.example.com/", "/api/recordings"), "https://d.example.com/api/recordings");
  assert.equal(joinUrl("https://d.example.com///", "/api/recordings"), "https://d.example.com/api/recordings");
  assert.equal(joinUrl("https://d.example.com", "/api/recordings"), "https://d.example.com/api/recordings");
});

test("returns everything when returnAll is set", () => {
  assert.equal(applyLimit([1, 2, 3], true, 2).length, 3);
});

test("truncates to the limit otherwise", () => {
  assert.deepEqual(applyLimit([1, 2, 3], false, 2), [1, 2]);
});

test("tolerates a non-array payload rather than throwing", () => {
  assert.deepEqual(applyLimit(undefined as unknown as number[], false, 2), []);
});
