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
