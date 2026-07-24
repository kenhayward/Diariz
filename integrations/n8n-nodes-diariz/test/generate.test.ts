import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import {
  buildResources,
  EXCLUDED_TAGS,
  firstParagraph,
  slug,
  toResourceName,
  type OpenApiDocument,
} from "../scripts/generate";

const doc = JSON.parse(
  readFileSync(path.join(process.cwd(), "nodes", "Diariz", "generated", "openapi.snapshot.json"), "utf8"),
) as OpenApiDocument;

const resources = buildResources(doc);

test("splits a PascalCase tag into a readable singular resource name", () => {
  assert.equal(toResourceName("SectionAttachments"), "Section Attachment");
  assert.equal(toResourceName("Recordings"), "Recording");
  assert.equal(toResourceName("Storage"), "Storage");
  assert.equal(toResourceName("ApiTokens"), "Api Token");
});

test("takes the first paragraph and strips Markdown", () => {
  assert.equal(
    firstParagraph("The **50 most recent** deliveries.\n\nFailed ones are retried."),
    "The 50 most recent deliveries.",
  );
  assert.equal(firstParagraph("Uses `status` and [a link](https://x/y)."), "Uses status and a link.");
});

test("replaces fancy dashes, which are banned in user-facing copy", () => {
  assert.equal(firstParagraph("Runs a formula – then stops."), "Runs a formula - then stops.");
});

test("makes a stable camelCase operation slug", () => {
  assert.equal(slug("Read a formula document"), "readAFormulaDocument");
  assert.equal(slug("List your automations"), "listYourAutomations");
});

test("excludes the Auth resource so no workflow holds an account password", () => {
  assert.deepEqual(EXCLUDED_TAGS, ["Auth"]);
  assert.ok(!resources.some((r) => r.tag === "Auth"));
});

test("covers every published tag except the exclusions", () => {
  const tags = new Set<string>();
  for (const item of Object.values(doc.paths)) {
    for (const op of Object.values(item)) {
      if (op && typeof op === "object" && Array.isArray(op.tags)) {
        for (const t of op.tags as string[]) tags.add(t);
      }
    }
  }
  assert.ok(tags.size > 20, "the snapshot looks empty");
  for (const tag of tags) {
    if (EXCLUDED_TAGS.includes(tag)) continue;
    assert.ok(
      resources.some((r) => r.tag === tag),
      `no generated resource for ${tag}`,
    );
  }
});

test("gives every operation a display name and a description", () => {
  for (const r of resources) {
    for (const op of r.operations) {
      assert.ok(op.displayName.length > 0, `${r.tag}.${op.value} has no display name`);
      assert.ok(op.description.length > 0, `${r.tag}.${op.value} has no description`);
    }
  }
});

test("gives every operation within a resource a unique slug", () => {
  for (const r of resources) {
    const values = r.operations.map((o) => o.value);
    assert.equal(new Set(values).size, values.length, `duplicate operation slug in ${r.tag}`);
  }
});

test("records path parameters for every templated segment", () => {
  for (const r of resources) {
    for (const op of r.operations) {
      const inPath = [...op.path.matchAll(/\{(\w+)\}/g)].map((m) => m[1]);
      assert.deepEqual(op.pathParams, inPath, `${r.tag}.${op.value} path params do not match its template`);
    }
  }
});

test("marks write operations as carrying a body", () => {
  const all = resources.flatMap((r) => r.operations);
  assert.ok(all.some((o) => o.hasBody), "no operation was detected as having a request body");
  assert.ok(!all.some((o) => o.method === "GET" && o.hasBody), "a GET should never carry a body");
});

test("uses plain hyphens in all generated copy", () => {
  assert.ok(!/[–—]/.test(JSON.stringify(resources)));
});
