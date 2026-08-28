import { describe, it, expect } from "vitest";
import { ARCHIVE } from "./archive";

/// The archive is the bulk of the release history, and the only thing keeping it out of the initial
/// bundle is *who imports it*. That is invisible: adding `import { ARCHIVE } from ...` to an eagerly
/// loaded module compiles, type-checks, renders correctly and passes every other test in this repo,
/// while quietly putting ~170 KB gzip back on every page load for every user. Nothing else would catch
/// it, so the module graph is asserted directly.
const sources = import.meta.glob("../../**/*.{ts,tsx}", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

/// Vite keys a glob relative to the file that declares it, so these are paths from `lib/releaseNotes/`,
/// not from `src/`. Getting that wrong is silent - a lookup returns `undefined`, and a `not.toMatch`
/// against `undefined` passes - so the keys are named once here and their existence asserted first.
const ARCHIVE_MODULE = "./archive.ts";
const BARREL = "./index.ts";
const APP = "../../App.tsx";
const DRILL_DOWN_PAGE = "../../pages/ReleaseNotes.tsx";

describe("the release archive stays out of the initial bundle", () => {
  it("scans the files it claims to check", () => {
    // Every assertion below is a negative or a filter over `sources`. If the scan silently missed a
    // file, or keyed one differently than expected, they would all pass while checking nothing.
    for (const key of [ARCHIVE_MODULE, BARREL, APP, DRILL_DOWN_PAGE]) {
      expect(sources[key], `${key} was not picked up by the source scan`).toBeDefined();
    }
    expect(ARCHIVE.length).toBeGreaterThan(100);
  });

  it("is imported by exactly one page", () => {
    const importers = Object.entries(sources)
      .filter(([key]) => !key.includes(".test."))
      .filter(([, src]) => /from\s+"[^"]*archive"/.test(src))
      .map(([key]) => key)
      .sort();

    expect(importers, "only the drill-down page may import the archive").toEqual([DRILL_DOWN_PAGE]);
  });

  it("is not re-exported through the barrel", () => {
    // Re-exporting ARCHIVE here would pull the archive into every chunk that touches the barrel - which
    // includes the About box, i.e. the whole app - defeating the split without any importer looking
    // wrong. Matched as a module specifier rather than the bare word, because the barrel's own comment
    // explains why the archive is absent and a substring check would fire on the explanation.
    expect(sources[BARREL]).not.toMatch(/from\s+"\.\/archive"/);
  });

  it("hangs off a lazy route rather than a static page import", () => {
    expect(sources[APP]).toMatch(/const ReleaseNotes = lazy\(\(\) => import\("\.\/pages\/ReleaseNotes"\)\)/);
    expect(sources[APP], "a static page import would drag the archive back into the main bundle").not.toMatch(
      /^import ReleaseNotes from/m,
    );
  });
});
