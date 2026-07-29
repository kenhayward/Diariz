import { test } from "node:test";
import assert from "node:assert/strict";
import type { IDataObject, IHookFunctions } from "n8n-workflow";
import { EVENT_OPTIONS } from "../nodes/Diariz/events";
import { DiarizTrigger } from "../nodes/Diariz/DiarizTrigger.node";

/// A minimal IHookFunctions good enough to drive the registration lifecycle. It records every HTTP call so a
/// test can assert on the body Diariz is actually sent.
function hookContext(opts: {
  params?: IDataObject;
  staticData?: IDataObject;
  existing?: IDataObject[];
  url?: string;
}) {
  const calls: { method: string; path: string; body?: IDataObject }[] = [];
  const staticData = opts.staticData ?? {};
  const params: IDataObject = { events: ["recording.summarized"], ...(opts.params ?? {}) };

  const ctx = {
    calls,
    staticData,
    getWorkflowStaticData: () => staticData,
    getNodeWebhookUrl: () => opts.url ?? "https://n8n.example.com/webhook/abc",
    getNodeParameter: (name: string, fallback?: unknown) =>
      name in params ? params[name] : fallback,
    getWorkflow: () => ({ name: "Test Workflow" }),
    getNode: () => ({ name: "Diariz Trigger" }),
    helpers: {
      httpRequestWithAuthentication: async (_cred: string, o: { method: string; url: string; body?: IDataObject }) => {
        const path = o.url.replace("https://diariz.example.com", "");
        calls.push({ method: o.method, path, body: o.body });
        if (o.method === "GET") return opts.existing ?? [];
        if (o.method === "POST") return { id: "sub-1", secret: "shh" };
        return {};
      },
    },
    getCredentials: async () => ({ baseUrl: "https://diariz.example.com" }),
  };
  return ctx as unknown as IHookFunctions & { calls: typeof calls; staticData: IDataObject };
}

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

// ---- Attendee contacts -------------------------------------------------------------------------------
//
// The subscription is owned by this node, not by the user: re-publishing a workflow deletes and recreates it.
// So a setting that lives only on the Diariz side is silently reset every time the workflow is edited, which
// is exactly what happened in testing - contacts stopped arriving with nothing in Diariz having changed. The
// setting has to live here, where the thing that owns the subscription can re-apply it.

test("offers an attendee-contacts option, off by default", () => {
  const d = new DiarizTrigger().description;
  const prop = d.properties.find((p) => p.name === "includeAttendeeContacts");
  assert.ok(prop, "the trigger should expose includeAttendeeContacts");
  assert.equal(prop!.type, "boolean");
  assert.equal(prop!.default, false, "contact details are personal data - opt in, never out");
});

test("sends the attendee-contacts choice when it registers", async () => {
  const t = new DiarizTrigger();
  const ctx = hookContext({ params: { includeAttendeeContacts: true } });

  await t.webhookMethods.default.create.call(ctx);

  const post = ctx.calls.find((c) => c.method === "POST");
  assert.ok(post, "expected a subscription to be created");
  assert.equal(post!.body!.includeAttendeeContacts, true);
});

test("defaults the attendee-contacts choice to off when it registers", async () => {
  const t = new DiarizTrigger();
  const ctx = hookContext({});

  await t.webhookMethods.default.create.call(ctx);

  assert.equal(ctx.calls.find((c) => c.method === "POST")!.body!.includeAttendeeContacts, false);
});

test("re-registers when the attendee-contacts choice no longer matches the subscription", async () => {
  const t = new DiarizTrigger();
  const url = "https://n8n.example.com/webhook/abc";
  const ctx = hookContext({
    params: { includeAttendeeContacts: true },
    staticData: { subscriptionId: "sub-1", secret: "shh" },
    existing: [{ id: "sub-1", url, includeAttendeeContacts: false }],
  });

  // Turning the option on has to take effect on a subscription that already exists, and create() is the only
  // place that can re-apply it - n8n calls it only when checkExists says no.
  assert.equal(await t.webhookMethods.default.checkExists.call(ctx), false);
});

test("leaves a matching subscription alone", async () => {
  const t = new DiarizTrigger();
  const url = "https://n8n.example.com/webhook/abc";
  const ctx = hookContext({
    params: { includeAttendeeContacts: true },
    staticData: { subscriptionId: "sub-1", secret: "shh" },
    existing: [{ id: "sub-1", url, includeAttendeeContacts: true }],
  });

  assert.equal(await t.webhookMethods.default.checkExists.call(ctx), true);
});
