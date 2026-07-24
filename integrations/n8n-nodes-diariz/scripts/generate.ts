import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

/// Generates the n8n action node's long tail of operations from the platform's own OpenAPI document.
/// The document carries a summary and a description on every endpoint, which become the operation's display
/// name and hint text - so the generated surface reads like it was hand-written.
///
/// The snapshot is produced by tests/Diariz.Api.Tests/OpenApiSnapshotTests.cs and committed, so neither
/// `npm install` nor the published package ever needs a running API.

export interface OpenApiOperation {
  tags?: string[];
  summary?: string;
  description?: string;
  parameters?: { name: string; in: string; required?: boolean; description?: string; schema?: { type?: string } }[];
  requestBody?: unknown;
}

export interface OpenApiDocument {
  paths: Record<string, Record<string, OpenApiOperation>>;
}

export interface GeneratedQueryParam {
  name: string;
  required: boolean;
  description: string;
}

export interface GeneratedOperation {
  value: string;
  displayName: string;
  description: string;
  method: string;
  path: string;
  pathParams: string[];
  queryParams: GeneratedQueryParam[];
  hasBody: boolean;
}

export interface GeneratedResource {
  tag: string;
  displayName: string;
  value: string;
  operations: GeneratedOperation[];
}

/// POST /api/auth/login takes an email and password. The node authenticates with a token credential, and
/// offering a password operation would invite users to put their account password into a workflow field.
/// Everything it returns is already covered by the credential, and Custom API Call still reaches it.
export const EXCLUDED_TAGS = ["Auth"];

const METHODS = ["get", "post", "put", "patch", "delete"];

/// "SectionAttachments" -> "Section Attachment". Singularises a trailing "s" but leaves "ss" alone.
export function toResourceName(tag: string): string {
  const words = tag.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return words.replace(/(\w)s$/, (match, last: string) => (last === "s" ? match : last));
}

/// Takes the first paragraph and strips the Markdown n8n would render literally.
export function firstParagraph(markdown: string): string {
  const first = (markdown ?? "").split(/\n\s*\n/)[0] ?? "";
  return first
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1") // links keep their text
    .replace(/\*\*/g, "")
    .replace(/`/g, "")
    .replace(/[–—]/g, "-") // fancy dashes are banned in user-facing copy
    .replace(/\s+/g, " ")
    .trim();
}

/// "Read a formula document" -> "readAFormulaDocument".
export function slug(summary: string): string {
  const words = (summary ?? "")
    .replace(/[^A-Za-z0-9 ]/g, " ")
    .split(/\s+/)
    .filter(Boolean);
  if (words.length === 0) return "operation";
  return words
    .map((w, i) => (i === 0 ? w.toLowerCase() : w[0].toUpperCase() + w.slice(1).toLowerCase()))
    .join("");
}

function camelTag(tag: string): string {
  return tag[0].toLowerCase() + tag.slice(1);
}

export function buildResources(doc: OpenApiDocument): GeneratedResource[] {
  const byTag = new Map<string, GeneratedOperation[]>();

  for (const [route, item] of Object.entries(doc.paths ?? {})) {
    for (const method of METHODS) {
      const op = item[method];
      if (!op || !Array.isArray(op.tags) || op.tags.length === 0) continue;
      const tag = op.tags[0];
      if (EXCLUDED_TAGS.includes(tag)) continue;

      const summary = op.summary?.trim() || `${method.toUpperCase()} ${route}`;
      const list = byTag.get(tag) ?? [];

      // Two endpoints in one resource can share a summary; keep slugs unique and stable by order.
      let value = slug(summary);
      if (list.some((o) => o.value === value)) {
        let n = 2;
        while (list.some((o) => o.value === `${value}${n}`)) n++;
        value = `${value}${n}`;
      }

      list.push({
        value,
        displayName: firstParagraph(summary),
        description: firstParagraph(op.description ?? summary),
        method: method.toUpperCase(),
        path: route,
        pathParams: [...route.matchAll(/\{(\w+)\}/g)].map((m) => m[1]),
        queryParams: (op.parameters ?? [])
          .filter((p) => p.in === "query")
          .map((p) => ({
            name: p.name,
            required: p.required === true,
            description: firstParagraph(p.description ?? p.name),
          })),
        hasBody: op.requestBody !== undefined && method !== "get",
      });
      byTag.set(tag, list);
    }
  }

  return [...byTag.entries()]
    .map(([tag, operations]) => ({
      tag,
      displayName: toResourceName(tag),
      value: camelTag(tag),
      operations: operations.sort((a, b) => a.displayName.localeCompare(b.displayName)),
    }))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
}

function main(): void {
  const root = process.cwd();
  const snapshotPath = path.join(root, "nodes", "Diariz", "generated", "openapi.snapshot.json");
  const outPath = path.join(root, "nodes", "Diariz", "generated", "index.ts");

  const doc = JSON.parse(readFileSync(snapshotPath, "utf8")) as OpenApiDocument;
  const resources = buildResources(doc);

  const banner =
    "// Generated by scripts/generate.ts from openapi.snapshot.json - do not edit.\n" +
    "// Run 'npm run generate' after the snapshot changes; CI fails the build if this drifts.\n";
  const body =
    `${banner}\nimport type { GeneratedResource } from "../generatedTypes";\n\n` +
    `const GENERATED: GeneratedResource[] = ${JSON.stringify(resources, null, 2)};\n\n` +
    "export default GENERATED;\n";

  writeFileSync(outPath, body, "utf8");
  const operations = resources.reduce((n, r) => n + r.operations.length, 0);
  process.stdout.write(`Generated ${resources.length} resources, ${operations} operations -> ${outPath}\n`);
}

// Only run when invoked directly, so importing the pure helpers in tests does not write files.
if (require.main === module) main();
