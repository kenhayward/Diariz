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

  it("gives every group a tab label and a column label that the catalogue actually defines", () => {
    // The tab strip and the matrix header need shorter labels than the full group name - "Minutes and
    // formulas" is three words too long for an 86px column. They are i18n keys, so a typo renders the raw
    // key to the administrator rather than failing anywhere; reading the catalogue is what catches it.
    const catalogue = JSON.parse(
      readFileSync(resolve(__dirname, "../../locales/en/account.json"), "utf8"),
    ) as Record<string, string>;

    // Every group gets a tab; only the assignable ones get a matrix column (the matrix's own "Default"
    // column is the default MODEL, not the ModelBase parameter scope).
    for (const g of GROUPS) expect(catalogue[g.short]).toBeTruthy();
    for (const g of ASSIGNABLE_GROUPS) expect(catalogue[g.column]).toBeTruthy();
  });

  it("does not warn about the sentinel values the API says are legal", () => {
    // LlmParameterLayers' own doc: "-1 is a legal, meaningful value for max_tokens (unlimited) and top_k
    // (disabled) on some OpenAI-compatible servers" - it is the reason the three-state design uses absence
    // and null rather than a sentinel. A lower bound of 0 on these would flag a deliberate, documented
    // choice as a mistake.
    for (const key of ["top_k", "max_tokens", "max_completion_tokens"]) {
      const spec = PARAMETERS.find((p) => p.key === key)!;
      expect(spec.min === undefined || spec.min <= -1).toBe(true);
    }
  });

  it("bounds every numeric parameter that has a documented range", () => {
    // A range is what the row shows when a value looks wrong, so an absent one means no guidance at all.
    for (const key of ["temperature", "top_p", "frequency_penalty", "presence_penalty", "timeout_seconds"]) {
      const spec = PARAMETERS.find((p) => p.key === key)!;
      expect(spec.min !== undefined || spec.max !== undefined).toBe(true);
    }
  });

  it("gives every parameter a label and a kind", () => {
    for (const p of PARAMETERS) {
      expect(p.label.length).toBeGreaterThan(0);
      expect(["number", "integer", "text", "boolean"]).toContain(p.kind);
    }
  });
});
