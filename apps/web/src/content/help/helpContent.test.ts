import { describe, it, expect } from "vitest";
import { parseArticle, DEFAULT_LOCALE } from "../../lib/help/parseArticle";
import { HELP_INDEX, HELP_SLUGS, HELP_ASSETS } from "../../lib/help/content";
import { HELP_GROUP_IDS } from "../../lib/help/groups";
import { localImageRefs } from "../../lib/help/images";

// The gate for authored help content. These are not tests of code so much as tests of the articles
// themselves - they are what make it safe to add content without re-reading the loader.

const rawFiles = import.meta.glob("./*/*.md", { eager: true, query: "?raw", import: "default" }) as Record<
  string,
  string
>;

/** Popover-sized: long enough for two or three sentences, short enough not to overflow the panel. */
const MAX_SUMMARY = 240;

/** Tab, LF, CR, and the printable ASCII range. */
const ASCII_ONLY = /^[\t\n\r\x20-\x7E]*$/;

describe("help content is ASCII only", () => {
  // A deliberate constraint: the docs render in a terminal-ish monospace context in places, get copied
  // into plain-text channels, and must satisfy CLAUDE.md's no-em-dash rule. Restricting to ASCII makes
  // all of that a single mechanical check rather than a review habit.
  for (const [path, raw] of Object.entries(rawFiles)) {
    it(`${path} contains only ASCII`, () => {
      if (ASCII_ONLY.test(raw)) return;
      // Point at the exact character so the failure is actionable, not just "a file is wrong".
      const lines = raw.split("\n");
      const problems: string[] = [];
      lines.forEach((line, i) => {
        for (const ch of line) {
          const code = ch.codePointAt(0) ?? 0;
          if (code > 0x7e || (code < 0x20 && ch !== "\t" && ch !== "\r")) {
            problems.push(`line ${i + 1}: ${JSON.stringify(ch)} (U+${code.toString(16).toUpperCase()})`);
          }
        }
      });
      expect(problems).toEqual([]);
    });
  }
});

describe("help article metadata", () => {
  it("finds at least one article", () => {
    expect(HELP_INDEX.length).toBeGreaterThan(0);
  });

  for (const [path, raw] of Object.entries(rawFiles)) {
    const slug = /\/([^/]+)\.md$/.exec(path)?.[1] ?? path;
    const a = parseArticle(slug, "en", raw);

    it(`${slug} declares a title, a summary and a known group`, () => {
      expect(a.title.length).toBeGreaterThan(0);
      // The title must be authored, not the slug fallback - otherwise the front matter is missing.
      expect(a.title).not.toBe(slug);
      expect(a.summary.length).toBeGreaterThan(0);
      expect(a.summary.length).toBeLessThanOrEqual(MAX_SUMMARY);
      expect(HELP_GROUP_IDS).toContain(a.group);
    });

    it(`${slug} has a body`, () => {
      expect(a.body.trim().length).toBeGreaterThan(0);
    });
  }
});

describe("help screenshots", () => {
  // A renamed or uncommitted screenshot renders as a broken image, which is worse than no image at all.
  // `resolveImages` leaves an unresolvable path untouched precisely so this test can name it.
  //
  // One test over all articles rather than one per article: articles without screenshots are the norm
  // for now, and a per-article loop would generate an empty suite.
  it("every referenced screenshot exists", () => {
    const assetPaths = new Set(Object.keys(HELP_ASSETS));
    const missing: string[] = [];

    for (const [path, raw] of Object.entries(rawFiles)) {
      const m = /\/([^/]+)\/([^/]+)\.md$/.exec(path);
      const locale = m?.[1] ?? DEFAULT_LOCALE;
      const slug = m?.[2] ?? path;
      for (const ref of localImageRefs(parseArticle(slug, locale, raw).body)) {
        if (!assetPaths.has(`${locale}/${ref}`) && !assetPaths.has(`${DEFAULT_LOCALE}/${ref}`)) {
          missing.push(`${slug}: ${ref}`);
        }
      }
    }

    expect(missing).toEqual([]);
  });
});

describe("contextual help topics", () => {
  // Every `<HelpButton topic="..." />` in the app must resolve to a real article, or a user clicks a `?`
  // and gets nothing. Scanning the source as text is crude but catches the mistake at build time, which
  // rendering tests for individual components never would.
  const sources = import.meta.glob("../../**/*.tsx", { eager: true, query: "?raw", import: "default" }) as Record<
    string,
    string
  >;

  const used = new Map<string, string[]>();
  for (const [path, src] of Object.entries(sources)) {
    if (path.includes(".test.")) continue;
    for (const m of src.matchAll(/<HelpButton[^>]*?\btopic="([^"]+)"/g)) {
      used.set(m[1], [...(used.get(m[1]) ?? []), path]);
    }
  }

  it("every topic referenced in the UI has an article", () => {
    const missing = [...used.entries()]
      .filter(([topic]) => !HELP_SLUGS.includes(topic))
      .map(([topic, paths]) => `${topic} (used in ${paths.join(", ")})`);
    expect(missing).toEqual([]);
  });
});
