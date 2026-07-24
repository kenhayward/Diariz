import { test } from "node:test";
import assert from "node:assert/strict";
import type { INodeProperties } from "n8n-workflow";
import { DiarizApi } from "../credentials/DiarizApi.credentials";

test("authenticates with a bearer token", () => {
  const c = new DiarizApi();
  assert.equal(c.name, "diarizApi");
  assert.equal(
    (c.authenticate.properties.headers as Record<string, string>).Authorization,
    "=Bearer {{$credentials.apiToken}}",
  );
});

test("tests the credential against the profile endpoint", () => {
  const c = new DiarizApi();
  assert.equal(c.test.request.url, "/api/user/profile");
  assert.ok(String(c.test.request.baseURL).includes("$credentials.baseUrl"));
});

test("warns when the platform has API access or automations turned off", () => {
  const c = new DiarizApi();
  const keys = (c.test.rules ?? []).map((r: unknown) => (r as { properties: { key: string } }).properties.key);
  assert.ok(keys.includes("apiAccessEnabled"));
  assert.ok(keys.includes("webhooksEnabled"));
});

test("keeps the API token out of view", () => {
  const c = new DiarizApi();
  const token = c.properties.find((p: INodeProperties) => p.name === "apiToken");
  assert.ok(token);
  assert.equal(token!.typeOptions?.password, true);
});

test("uses plain hyphens in all credential copy", () => {
  const c = new DiarizApi();
  const text = JSON.stringify([c.properties, c.test]);
  assert.ok(!/[–—]/.test(text), "found an en or em dash in user-facing copy");
});
