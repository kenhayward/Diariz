import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/// CLAUDE.md: no em or en dashes in user-facing text - "user feedback on fancy dashes is negative". The
/// rule was enforced in exactly one place, `content/help/helpContent.test.ts`, which requires help articles
/// to be ASCII and catches dashes there as a side effect. Everything else the rule names was unguarded, and
/// the About box and the chat `/help` output had drifted (issue #673).
///
/// Comments are deliberately exempt, which is what makes this awkward: the source carries dozens of
/// perfectly legitimate dashes in prose comments. Grepping the file would drown the real ones, so the
/// source scan strips comments first and checks what is left.
const REPO = resolve(__dirname, "../../../..");
const FANCY = /[–—]/;

const read = (p: string) => readFileSync(resolve(REPO, p), "utf8");

/// Strip comments so the exempt ones do not drown the real hits, without needing to parse TypeScript.
///
/// Block comments go first, across lines. Line comments are then judged **one line at a time**: scan from
/// the start of the line tracking quotes, and a `//` reached outside a quote starts a comment. Deliberately
/// per line rather than over the whole file, because a whole-file scan desynchronises on a regex literal
/// containing a quote - the `.replace(/"/g, ...)` in clipboard.ts opened a string that swallowed the rest
/// of the file and turned a doc comment three lines later into a false positive. Per line, that mistake
/// cannot travel beyond the line that causes it.
export function stripComments(source: string): string {
  const withoutBlocks = source.replace(/\/\*[\s\S]*?\*\//g, " ");
  return withoutBlocks
    .split("\n")
    .map((line) => {
      let quote: string | null = null;
      for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quote) {
          if (c === "\\") i++;
          else if (c === quote) quote = null;
        } else if (c === '"' || c === "'" || c === "`") {
          quote = c;
        } else if (c === "/" && line[i + 1] === "/") {
          return line.slice(0, i);
        }
      }
      return line;
    })
    .join("\n");
}

function offendingLines(text: string): string[] {
  return text
    .split("\n")
    .map((line, n) => [n + 1, line] as const)
    .filter(([, line]) => FANCY.test(line))
    .map(([n, line]) => `line ${n}: ${line.trim().slice(0, 100)}`);
}

describe("no em or en dashes in user-facing text", () => {
  const catalogs = import.meta.glob("../locales/*/*.json", { eager: true, query: "?raw", import: "default" }) as Record<
    string,
    string
  >;

  it.each(Object.keys(catalogs))("%s", (path) => {
    expect(offendingLines(catalogs[path])).toEqual([]);
  });

  /// The API has its own catalogs, read by ExportLocalizer for emailed transcripts and minutes. Same rule,
  /// same reader-facing text, and nothing was checking them either.
  it.each([
    ["src/Diariz.Api/locales/en/exports.json"],
    ["src/Diariz.Api/locales/de/exports.json"],
    ["src/Diariz.Api/locales/es/exports.json"],
    ["src/Diariz.Api/locales/fr/exports.json"],
  ])("%s", (path) => {
    expect(offendingLines(read(path))).toEqual([]);
  });

  /// Release notes and the About-box capabilities table.
  it("releases.ts", () => {
    expect(offendingLines(stripComments(read("apps/web/src/lib/releases.ts")))).toEqual([]);
  });

  /// Every source file's strings and JSX text. Comments are stripped first, per the rule.
  it("no source file renders a fancy dash", () => {
    const sources = import.meta.glob("../**/*.{ts,tsx}", { eager: true, query: "?raw", import: "default" }) as Record<
      string,
      string
    >;

    const offenders: string[] = [];
    for (const [path, src] of Object.entries(sources)) {
      if (path.includes(".test.")) continue;
      for (const line of offendingLines(stripComments(src))) offenders.push(`${path.replace("../", "")} ${line}`);
    }

    expect(offenders, "use a plain hyphen in user-facing text (CLAUDE.md)").toEqual([]);
  });
});
