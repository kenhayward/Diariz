import { test } from "node:test";
import assert from "node:assert/strict";
import type { IDataObject, IHookFunctions } from "n8n-workflow";
import { EVENT_OPTIONS, PLATFORM_EVENT_OPTIONS } from "../nodes/Diariz/events";
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

// ---- Platform scope ----------------------------------------------------------------------------------
// A platform subscription is a different object at a different endpoint, not a personal one with a flag:
// /api/admin/webhooks requires a Platform Administrator and is the only scope that may carry
// feedback.submitted. Getting the endpoint wrong is silent - the call simply 400s on activation.

test("keeps platform-only events out of the personal event list", () => {
  // Offering this in Personal scope would produce a node that always fails to activate, and the server
  // refuses it for a reason: a personal subscription would deliver another user's words to its owner.
  assert.ok(!EVENT_OPTIONS.some((o) => o.value === "feedback.submitted"));
  assert.ok(PLATFORM_EVENT_OPTIONS.some((o) => o.value === "feedback.submitted"));
  // The platform list is a superset, so an admin never loses an event by choosing Platform.
  for (const o of EVENT_OPTIONS) {
    assert.ok(PLATFORM_EVENT_OPTIONS.some((p) => p.value === o.value), `platform list missing ${o.value}`);
  }
});

test("registers a platform subscription at the admin endpoint, with its signals and text opt-in", async () => {
  const ctx = hookContext({
    params: {
      scope: "platform",
      platformEvents: ["feedback.submitted"],
      signalFilter: ["triage"],
      includeFeedbackText: true,
    },
  });
  await new DiarizTrigger().webhookMethods.default.create.call(ctx);

  const post = ctx.calls.find((c) => c.method === "POST")!;
  assert.equal(post.path, "/api/admin/webhooks");
  assert.deepEqual(post.body!.eventTypes, ["feedback.submitted"]);
  assert.deepEqual(post.body!.signalFilter, ["triage"]);
  assert.equal(post.body!.includeFeedbackText, true);
  // Not accepted by the platform create endpoint - sending it would be noise.
  assert.ok(!("includeAttendeeContacts" in post.body!));
  // Recorded so delete() and checkExists() know which endpoint owns this subscription.
  assert.equal(ctx.staticData.scope, "platform");
});

test("reads the platform event list, not the personal one", async () => {
  const ctx = hookContext({
    params: {
      scope: "platform",
      events: ["recording.summarized"], // the personal field, which must be ignored here
      platformEvents: ["feedback.submitted"],
      signalFilter: [],
    },
  });
  await new DiarizTrigger().webhookMethods.default.create.call(ctx);

  const post = ctx.calls.find((c) => c.method === "POST")!;
  assert.deepEqual(post.body!.eventTypes, ["feedback.submitted"]);
});

test("allows a feedback-only platform subscription with no signals", async () => {
  // feedback.submitted is exempt from the publisher's signal gate, so requiring one would force the admin
  // to invent a meaningless signal. Mirrors PlatformWebhooksController.Validate.
  const ctx = hookContext({
    params: { scope: "platform", platformEvents: ["feedback.submitted"], signalFilter: [] },
  });
  await new DiarizTrigger().webhookMethods.default.create.call(ctx);
  assert.ok(ctx.calls.some((c) => c.method === "POST" && c.path === "/api/admin/webhooks"));
});

test("refuses a signal-routed platform event with no signals, naming the cause", async () => {
  const ctx = hookContext({
    params: {
      scope: "platform",
      platformEvents: ["feedback.submitted", "recording.summarized"],
      signalFilter: [],
    },
  });
  await assert.rejects(
    () => new DiarizTrigger().webhookMethods.default.create.call(ctx),
    /Workflow Signal/,
  );
  // Nothing was created, so there is no orphan to clean up.
  assert.ok(!ctx.calls.some((c) => c.method === "POST"));
});

test("deletes from the endpoint the subscription was CREATED at, not the one now selected", async () => {
  // After a scope change the subscription still lives where it was made. Deleting from the new endpoint
  // would 404 and leave it delivering forever.
  const ctx = hookContext({
    params: { scope: "personal" },
    staticData: { subscriptionId: "sub-1", secret: "shh", scope: "platform" },
  });
  await new DiarizTrigger().webhookMethods.default.delete.call(ctx);

  assert.deepEqual(
    ctx.calls.map((c) => `${c.method} ${c.path}`),
    ["DELETE /api/admin/webhooks/sub-1"],
  );
  assert.equal(ctx.staticData.scope, undefined);
});

test("rebuilds when the scope changed, without trusting the old endpoint", async () => {
  const ctx = hookContext({
    params: { scope: "platform", platformEvents: ["feedback.submitted"] },
    staticData: { subscriptionId: "sub-1", secret: "shh", scope: "personal" },
    existing: [{ id: "sub-1", url: "https://n8n.example.com/webhook/abc" }],
  });
  const exists = await new DiarizTrigger().webhookMethods.default.checkExists.call(ctx);

  assert.equal(exists, false);
  // Decided from recorded state alone - listing the new endpoint proves nothing about the old subscription.
  assert.equal(ctx.calls.length, 0);
});

test("re-registers when the feedback-text choice no longer matches the subscription", async () => {
  const ctx = hookContext({
    params: { scope: "platform", platformEvents: ["feedback.submitted"], includeFeedbackText: true },
    staticData: { subscriptionId: "sub-1", secret: "shh", scope: "platform" },
    existing: [
      { id: "sub-1", url: "https://n8n.example.com/webhook/abc", includeFeedbackText: false },
    ],
  });
  assert.equal(await new DiarizTrigger().webhookMethods.default.checkExists.call(ctx), false);
  assert.equal(ctx.calls[0].path, "/api/admin/webhooks");
});

test("leaves a matching platform subscription alone", async () => {
  const ctx = hookContext({
    params: { scope: "platform", platformEvents: ["feedback.submitted"], includeFeedbackText: true },
    staticData: { subscriptionId: "sub-1", secret: "shh", scope: "platform" },
    existing: [
      { id: "sub-1", url: "https://n8n.example.com/webhook/abc", includeFeedbackText: true },
    ],
  });
  assert.equal(await new DiarizTrigger().webhookMethods.default.checkExists.call(ctx), true);
});

test("offers a scope parameter that defaults to personal", () => {
  // The default matters for compatibility: a node saved before 0.177.0 has no scope stored, so it must
  // fall back to exactly what it did before.
  const scope = new DiarizTrigger().description.properties.find((p) => p.name === "scope");
  assert.ok(scope);
  assert.equal(scope!.default, "personal");
});

test("uses plain hyphens in the platform event copy too", () => {
  const text = JSON.stringify(PLATFORM_EVENT_OPTIONS);
  assert.ok(!/[–—]/.test(text), "found an en or em dash in user-facing copy");
});
