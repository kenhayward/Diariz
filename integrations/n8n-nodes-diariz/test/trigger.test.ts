import { test } from "node:test";
import assert from "node:assert/strict";
import { EVENT_OPTIONS } from "../nodes/Diariz/events";
import { DiarizTrigger } from "../nodes/Diariz/DiarizTrigger.node";

test("offers all nine subscribable events", () => {
  assert.equal(EVENT_OPTIONS.length, 9);
  const values = EVENT_OPTIONS.map((o) => o.value);
  for (const expected of [
    "recording.created",
    "recording.transcribed",
    "recording.transcription_failed",
    "recording.summarized",
    "recording.minutes_ready",
    "recording.action_items_ready",
    "recording.tags_ready",
    "formula_result.completed",
    "formula_result.failed",
  ]) {
    assert.ok(values.includes(expected), `missing ${expected}`);
  }
});

test("never offers the internal ping event", () => {
  assert.ok(!EVENT_OPTIONS.some((o) => o.value === "webhook.ping"));
});

test("requests the raw body so signatures can be verified", () => {
  const webhook = new DiarizTrigger().description.webhooks![0];
  // Without this the body is re-serialised by n8n and the HMAC can never match.
  assert.equal((webhook as unknown as { rawBody: boolean }).rawBody, true);
  assert.equal(webhook.httpMethod, "POST");
});

test("declares the credential and is a trigger with no inputs", () => {
  const d = new DiarizTrigger().description;
  assert.deepEqual(d.credentials, [{ name: "diarizApi", required: true }]);
  assert.deepEqual(d.inputs, []);
  assert.deepEqual(d.group, ["trigger"]);
});

test("exposes the events and simplify parameters", () => {
  const d = new DiarizTrigger().description;
  const events = d.properties.find((p) => p.name === "events");
  assert.ok(events);
  assert.equal(events!.type, "multiOptions");
  assert.equal(events!.required, true);
  assert.ok(d.properties.some((p) => p.name === "simplify" && p.default === true));
});

test("implements the full self-registration lifecycle", () => {
  const t = new DiarizTrigger();
  assert.equal(typeof t.webhookMethods.default.create, "function");
  assert.equal(typeof t.webhookMethods.default.delete, "function");
  assert.equal(typeof t.webhookMethods.default.checkExists, "function");
});

test("uses plain hyphens in every user-facing string", () => {
  const text = JSON.stringify(new DiarizTrigger().description) + JSON.stringify(EVENT_OPTIONS);
  assert.ok(!/[–—]/.test(text), "found an en or em dash in user-facing copy");
});
