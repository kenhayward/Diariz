import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, it, expect } from "vitest";
import { PARAMETERS, GROUPS, ASSIGNABLE_GROUPS } from "./parameterSchema";

/// These lists are duplicated from the API - the parameter keys from `LlmParameterLayers.ParameterNames`
/// and the group names from the `LlmCallGroup` enum. The API rejects any key or group it does not
/// recognise, so a drift here becomes a 400 the administrator cannot act on, and an omission here becomes a
/// parameter no one can set. Nothing else compares the two sides, so these tests read the C# and do it.
const API_ROOT = resolve(__dirname, "../../../../../src");

function read(path: string): string {
  return readFileSync(resolve(API_ROOT, path), "utf8");
}

describe("parameterSchema", () => {
  it("covers exactly the parameters the API accepts", () => {
    const source = read("Diariz.Api/Services/Llm/LlmParameterLayers.cs");

    // The `public const string X = "y";` declarations are the API's whole vocabulary.
    const apiKeys = [...source.matchAll(/public const string \w+ = "([a-z_]+)";/g)]
      .map((m) => m[1])
      .sort();

    expect(PARAMETERS.map((p) => p.key).sort()).toEqual(apiKeys);
  });

  it("covers exactly the call groups the API defines", () => {
    const source = read("Diariz.Domain/Entities/LlmCallGroup.cs");

    // Enum members look like `Tags = 1,` - the assignment is what distinguishes them from prose in the
    // doc comments above them.
    const apiGroups = [...source.matchAll(/^\s{4}(\w+) = \d+,/gm)].map((m) => m[1]).sort();

    expect(GROUPS.map((g) => g.key).sort()).toEqual(apiGroups);
  });

  it("excludes ModelBase from the assignable groups", () => {
    // ModelBase is a parameter scope, not a call type. The API returns 400 for an attempt to assign it, so
    // offering it in the UI would offer an action that can only fail.
    expect(ASSIGNABLE_GROUPS.map((g) => g.key)).not.toContain("ModelBase");
    expect(ASSIGNABLE_GROUPS).toHaveLength(GROUPS.length - 1);
  });

  it("gives every parameter a label and a kind", () => {
    for (const p of PARAMETERS) {
      expect(p.label.length).toBeGreaterThan(0);
      expect(["number", "integer", "text", "boolean"]).toContain(p.kind);
    }
  });
});
