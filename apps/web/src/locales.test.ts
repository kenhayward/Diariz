import { describe, it, expect } from "vitest";
import metadata from "./locales/languages.json";

// Load every catalog the same way the app does, so this gate covers exactly what ships.
const modules = import.meta.glob("./locales/*/*.json", { eager: true }) as Record<string, { default: object }>;

interface Catalog {
  lng: string;
  ns: string;
  data: Record<string, unknown>;
}

const catalogs: Catalog[] = [];
for (const path in modules) {
  const m = /\/locales\/([^/]+)\/([^/]+)\.json$/.exec(path);
  if (!m) continue;
  catalogs.push({ lng: m[1], ns: m[2], data: modules[path].default as Record<string, unknown> });
}

const locales = [...new Set(catalogs.map((c) => c.lng))];
const namespaces = [...new Set(catalogs.map((c) => c.ns))];
const get = (lng: string, ns: string) => catalogs.find((c) => c.lng === lng && c.ns === ns)?.data;

/** Flattened, sorted key list for a (possibly nested) catalog object. */
function keysOf(obj: Record<string, unknown>, prefix = ""): string[] {
  const out: string[] = [];
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v && typeof v === "object" && !Array.isArray(v)) out.push(...keysOf(v as Record<string, unknown>, key));
    else out.push(key);
  }
  return out.sort();
}

/** Flattened (key → leaf value) pairs for a (possibly nested) catalog object. */
function leavesOf(obj: Record<string, unknown>, prefix = ""): [string, unknown][] {
  const out: [string, unknown][] = [];
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v && typeof v === "object" && !Array.isArray(v)) out.push(...leavesOf(v as Record<string, unknown>, key));
    else out.push([key, v]);
  }
  return out;
}

const metaCodes = new Set((metadata as { code: string }[]).map((l) => l.code));

describe("locale catalogs (merge gate)", () => {
  it("ships English as the authoritative base", () => {
    expect(locales).toContain("en");
    for (const ns of namespaces) expect(get("en", ns), `en/${ns}.json missing`).toBeDefined();
  });

  it("registers every locale folder in languages.json", () => {
    for (const lng of locales) {
      expect(metaCodes.has(lng), `languages.json has no entry for "${lng}"`).toBe(true);
    }
  });

  for (const lng of locales.filter((l) => l !== "en")) {
    describe(`locale "${lng}"`, () => {
      for (const ns of namespaces) {
        it(`${ns}.json matches en exactly (no missing or extra keys)`, () => {
          const enKeys = keysOf(get("en", ns)!);
          const data = get(lng, ns);
          expect(data, `${lng}/${ns}.json is missing`).toBeDefined();
          expect(keysOf(data!)).toEqual(enKeys);
        });

        it(`${ns}.json has no empty values`, () => {
          const data = get(lng, ns)!;
          for (const [k, v] of leavesOf(data)) {
            expect(typeof v === "string" && v.trim().length > 0, `${lng}/${ns}.json → "${k}" is empty`).toBe(true);
          }
        });
      }
    });
  }
});
