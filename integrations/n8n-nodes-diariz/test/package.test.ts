import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";

// Resolved from the package root (npm scripts run there), so it survives the compiled test layout.
const pkg = JSON.parse(readFileSync(path.join(process.cwd(), "package.json"), "utf8"));

test("declares no runtime dependencies (n8n verification requirement)", () => {
  assert.deepEqual(pkg.dependencies ?? {}, {});
});

test("is discoverable as an n8n community node package", () => {
  assert.equal(pkg.name, "n8n-nodes-diariz");
  assert.equal(pkg.license, "MIT");
  assert.ok(pkg.keywords.includes("n8n-community-node-package"));
  assert.equal(pkg.n8n.n8nNodesApiVersion, 1);
});

test("registers both nodes and the credential", () => {
  assert.deepEqual(pkg.n8n.credentials, ["dist/credentials/DiarizApi.credentials.js"]);
  assert.deepEqual(pkg.n8n.nodes, [
    "dist/nodes/Diariz/Diariz.node.js",
    "dist/nodes/Diariz/DiarizTrigger.node.js",
  ]);
});

test("publishes only the built output", () => {
  assert.deepEqual(pkg.files, ["dist"]);
});
