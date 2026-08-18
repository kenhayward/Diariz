import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, it, expect } from "vitest";

/// `LlmCallKind` is duplicated from the API as a string union, because the API serializes the enum by name
/// in both directions (rows report it, and the usage-log filter sends it back). Nothing else compares the
/// two, so a member added on the server is invisible here until someone notices the filter cannot offer it
/// - and the label lookup, which is `t(\`llmUsageKind${kind}\`)`, renders the raw key at the user.
///
/// This is the same guard `parameterSchema.test.ts` applies to the parameter and group names, for the same
/// reason and in the same way: read the C#.
const REPO = resolve(__dirname, "../../../..");

function read(path: string): string {
  return readFileSync(resolve(REPO, path), "utf8");
}

describe("LlmCallKind", () => {
  const apiKinds = [
    ...read("src/Diariz.Domain/Entities/LlmCallKind.cs").matchAll(/^\s{4}(\w+) = \d+,/gm),
  ].map((m) => m[1]);

  const webKinds = (() => {
    const source = read("apps/web/src/lib/types.ts");
    const union = source.slice(source.indexOf("export type LlmCallKind ="));
    return [...union.slice(0, union.indexOf(";")).matchAll(/"(\w+)"/g)].map((m) => m[1]);
  })();

  it("covers exactly the call kinds the API defines", () => {
    expect(apiKinds.length).toBeGreaterThan(10); // the read worked at all
    expect([...webKinds].sort()).toEqual([...apiKinds].sort());
  });

  it("gives every call kind a label in the catalogue", () => {
    // The usage log builds the key from the kind name, so a missing entry shows an administrator
    // "llmUsageKindAdminTest" rather than a word.
    const catalogue = JSON.parse(read("apps/web/src/locales/en/account.json")) as Record<string, string>;

    for (const kind of apiKinds) expect(catalogue[`llmUsageKind${kind}`]).toBeTruthy();
  });
});
