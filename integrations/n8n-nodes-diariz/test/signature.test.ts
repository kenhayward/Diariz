import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { verifyWebhookSignature } from "../nodes/Diariz/signature";

const SECRET = "whsec_test";
const RAW = JSON.stringify({ id: "evt_1" });
const TS = 1750000000;
const AT = new Date(TS * 1000);
// A fixed vector, computed once, so this asserts the algorithm rather than the implementation's own arithmetic.
const GOOD = "v1,g9yolrp7eji0RVHF8rkSQUeEuUrQ2My17b2XVTa87sU=";

const headers = (over: Record<string, string> = {}) => ({
  "webhook-id": "evt_1",
  "webhook-timestamp": String(TS),
  "webhook-signature": GOOD,
  ...over,
});

test("accepts a correctly signed delivery", () => {
  assert.deepEqual(
    verifyWebhookSignature({ secret: SECRET, headers: headers(), rawBody: RAW, now: AT }),
    { ok: true },
  );
});

test("rejects a tampered body", () => {
  const r = verifyWebhookSignature({ secret: SECRET, headers: headers(), rawBody: RAW + " ", now: AT });
  assert.equal(r.ok, false);
});

test("rejects the wrong secret", () => {
  const r = verifyWebhookSignature({ secret: "whsec_other", headers: headers(), rawBody: RAW, now: AT });
  assert.equal(r.ok, false);
});

test("rejects an empty secret rather than accepting anything", () => {
  const r = verifyWebhookSignature({ secret: "", headers: headers(), rawBody: RAW, now: AT });
  assert.equal(r.ok, false);
});

test("accepts when one of several space-delimited signatures matches", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: headers({ "webhook-signature": `v1,AAAA ${GOOD}` }),
    rawBody: RAW,
    now: AT,
  });
  assert.deepEqual(r, { ok: true });
});

test("rejects a replayed delivery outside the tolerance window", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: headers(),
    rawBody: RAW,
    now: new Date((TS + 601) * 1000),
  });
  assert.equal(r.ok, false);
  assert.match((r as { reason: string }).reason, /too old|timestamp/i);
});

test("rejects a timestamp too far in the future", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: headers(),
    rawBody: RAW,
    now: new Date((TS - 601) * 1000),
  });
  assert.equal(r.ok, false);
});

test("rejects a missing header rather than throwing", () => {
  const h: Record<string, string | undefined> = headers();
  delete h["webhook-signature"];
  const r = verifyWebhookSignature({ secret: SECRET, headers: h, rawBody: RAW, now: AT });
  assert.equal(r.ok, false);
});

test("rejects a non-numeric timestamp rather than throwing", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: headers({ "webhook-timestamp": "not-a-number" }),
    rawBody: RAW,
    now: AT,
  });
  assert.equal(r.ok, false);
});

test("reads headers case-insensitively", () => {
  const r = verifyWebhookSignature({
    secret: SECRET,
    headers: { "Webhook-Id": "evt_1", "Webhook-Timestamp": String(TS), "Webhook-Signature": GOOD },
    rawBody: RAW,
    now: AT,
  });
  assert.deepEqual(r, { ok: true });
});

// The cross-language contract. These vectors are produced by the .NET WebhookSigner
// (tests/Diariz.Api.Tests/WebhookSignerFixtureTests.cs); if either side drifts, one of the two suites fails
// here rather than every live delivery being silently rejected in production.
const vectors = JSON.parse(
  readFileSync(path.join(process.cwd(), "test", "fixtures", "signing-vectors.json"), "utf8"),
) as { vectors: { secret: string; webhookId: string; timestamp: number; body: string; signature: string }[] };

test("accepts every vector produced by the .NET signer", () => {
  assert.ok(vectors.vectors.length >= 3, "the fixture lost its vectors");
  for (const v of vectors.vectors) {
    const result = verifyWebhookSignature({
      secret: v.secret,
      headers: {
        "webhook-id": v.webhookId,
        "webhook-timestamp": String(v.timestamp),
        "webhook-signature": v.signature,
      },
      rawBody: v.body,
      now: new Date(v.timestamp * 1000),
    });
    assert.deepEqual(result, { ok: true }, `vector ${v.webhookId} failed`);
  }
});

test("rejects every .NET vector when the body is altered by one byte", () => {
  for (const v of vectors.vectors) {
    const result = verifyWebhookSignature({
      secret: v.secret,
      headers: {
        "webhook-id": v.webhookId,
        "webhook-timestamp": String(v.timestamp),
        "webhook-signature": v.signature,
      },
      rawBody: `${v.body} `,
      now: new Date(v.timestamp * 1000),
    });
    assert.equal(result.ok, false, `vector ${v.webhookId} should not have verified`);
  }
});
