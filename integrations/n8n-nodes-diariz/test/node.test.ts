import { test } from "node:test";
import assert from "node:assert/strict";
import type { INodeProperties, INodePropertyOptions } from "n8n-workflow";
import { Diariz } from "../nodes/Diariz/Diariz.node";
import { buildPath } from "../nodes/Diariz/generatedProperties";

const node = new Diariz();
const props = node.description.properties;

function optionValues(p: INodeProperties): string[] {
  return (p.options as INodePropertyOptions[]).map((o) => String(o.value));
}

test("declares the credential and a single main output", () => {
  assert.deepEqual(node.description.credentials, [{ name: "diarizApi", required: true }]);
  assert.deepEqual(node.description.outputs, ["main"]);
  assert.deepEqual(node.description.inputs, ["main"]);
});

test("exposes a resource selector covering the generated tail", () => {
  const resource = props.find((p) => p.name === "resource");
  assert.ok(resource);
  const values = optionValues(resource!);
  assert.ok(values.includes("rooms"));
  assert.ok(values.includes("userProfile"));
  assert.ok(values.includes("calendar"));
});

test("defaults to a resource that actually exists", () => {
  const resource = props.find((p) => p.name === "resource")!;
  assert.ok(optionValues(resource).includes(resource.default as string));
});

test("never offers the excluded Auth resource", () => {
  const resource = props.find((p) => p.name === "resource")!;
  assert.ok(!optionValues(resource).includes("auth"));
});

test("offers Custom API Call on every resource", () => {
  const operations = props.filter((p) => p.name === "operation");
  assert.ok(operations.length > 20, "expected one operation selector per resource");
  for (const op of operations) {
    assert.ok(
      optionValues(op).includes("customApiCall"),
      `missing custom call on ${JSON.stringify(op.displayOptions)}`,
    );
  }
});

test("substitutes path parameters", () => {
  assert.equal(
    buildPath("/api/recordings/{recordingId}/formula-results/{id}", { recordingId: "a", id: "b" }),
    "/api/recordings/a/formula-results/b",
  );
});

test("url-encodes a path parameter so a stray slash cannot escape the route", () => {
  assert.equal(buildPath("/api/recordings/{id}", { id: "a/b" }), "/api/recordings/a%2Fb");
});

test("rejects a missing path parameter rather than sending a literal brace", () => {
  assert.throws(() => buildPath("/api/recordings/{id}", {}), /id/);
  assert.throws(() => buildPath("/api/recordings/{id}", { id: "" }), /id/);
});

test("gives every operation an action, which the n8n linter requires", () => {
  for (const op of props.filter((p) => p.name === "operation")) {
    for (const o of op.options as INodePropertyOptions[]) {
      assert.ok((o as { action?: string }).action, `${String(o.value)} has no action`);
    }
  }
});

test("uses plain hyphens throughout the node description", () => {
  assert.ok(!/[–—]/.test(JSON.stringify(node.description)));
});

function fieldFor(name: string, resource: string, operation: string): INodeProperties | undefined {
  return props.find(
    (p) =>
      p.name === name &&
      (p.displayOptions?.show?.resource as string[] | undefined)?.includes(resource) &&
      (p.displayOptions?.show?.operation as string[] | undefined)?.includes(operation),
  );
}

test("lets the user pick a recording from a dropdown instead of typing a GUID", () => {
  const field = fieldFor("path_id", "recordings", "getARecording");
  assert.ok(field);
  assert.equal(field!.type, "options");
  assert.equal(field!.typeOptions?.loadOptionsMethod, "getRecordings");
});

test("leaves an id with no listing endpoint as a plain field", () => {
  const field = fieldFor("path_segmentId", "recordings", "editASegmentSText");
  assert.ok(field);
  assert.equal(field!.type, "string");
  assert.equal(field!.typeOptions?.loadOptionsMethod, undefined);
});

test("registers every loadOptions method the properties reference", () => {
  const referenced = new Set(
    props.map((p) => p.typeOptions?.loadOptionsMethod).filter((m): m is string => Boolean(m)),
  );
  assert.ok(referenced.size > 0);
  for (const method of referenced) {
    assert.equal(typeof (node.methods.loadOptions as Record<string, unknown>)[method], "function", method);
  }
});

test("offers Return All with a limit on list operations only", () => {
  assert.ok(fieldFor("returnAll", "recordings", "listRecordings"));
  const limit = fieldFor("limit", "recordings", "listRecordings");
  assert.ok(limit);
  assert.deepEqual(limit!.displayOptions?.show?.returnAll, [false]);
  // A single-record fetch must not offer it.
  assert.ok(!fieldFor("returnAll", "recordings", "getARecording"));
});

test("writes downloads to a binary field", () => {
  const field = fieldFor("binaryPropertyName", "recordings", "downloadTheTranscriptAsPlainText");
  assert.ok(field);
  assert.equal(field!.default, "data");
});

test("takes an upload from a binary field and offers the optional form fields", () => {
  assert.ok(fieldFor("binaryPropertyName", "recordings", "uploadARecording"));
  const options = fieldFor("uploadOptions", "recordings", "uploadARecording");
  assert.ok(options);
  const names = (options!.options as INodeProperties[]).map((o) => o.name);
  assert.deepEqual(names.sort(), ["roomId", "sectionId", "title"]);
});

test("never offers a JSON body on a multipart upload", () => {
  assert.ok(!fieldFor("body", "recordings", "uploadARecording"));
  assert.ok(!fieldFor("body", "attachments", "attachAFile"));
});

test("offers Wait for Completion on the formula run, with its interval and timeout", () => {
  const wait = fieldFor("waitForCompletion", "formulas", "runAFormulaOverARecording");
  assert.ok(wait);
  assert.equal(wait!.default, true);
  assert.ok(fieldFor("pollIntervalSeconds", "formulas", "runAFormulaOverARecording"));
  assert.ok(fieldFor("timeoutSeconds", "formulas", "runAFormulaOverARecording"));
});

test("asks the chat question through a dedicated field, not a raw JSON body", () => {
  assert.ok(fieldFor("chatQuestion", "chat", "askAQuestionAndStreamTheAnswer"));
  assert.ok(!fieldFor("body", "chat", "askAQuestionAndStreamTheAnswer"));
});
