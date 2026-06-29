// Merge gate: a *translation-only* PR may touch at most ONE non-English locale directory, so a
// translation contribution stays reviewable in isolation. A PR that also changes code, docs, the `en`
// base, or the language metadata is a "core" PR and is exempt (it may bootstrap several languages at
// once). Pass the base ref/sha as argv[2] (defaults to origin/main); the script diffs the branch.
//
// Usage: node scripts/check-single-locale.mjs [baseRef]
import { execFileSync } from "node:child_process";

const base = process.argv[2] || "origin/main";

let changed = [];
try {
  // execFile (no shell) so the base ref can't inject shell metacharacters.
  const out = execFileSync("git", ["diff", "--name-only", `${base}...HEAD`], { encoding: "utf8" });
  changed = out.split("\n").filter(Boolean);
} catch (e) {
  console.error(`Could not diff against ${base}: ${e.message}`);
  process.exit(1);
}

// Match apps/web/src/locales/<lang>/<file>.json (the per-language catalog folders).
const localeFile = /(?:^|\/)apps\/web\/src\/locales\/([^/]+)\/[^/]+\.json$/;
const langs = new Set();
let hasOtherChanges = false; // any change outside a non-en catalog folder → a core PR, exempt
for (const file of changed) {
  const m = localeFile.exec(file);
  if (m && m[1] !== "en") langs.add(m[1]);
  else hasOtherChanges = true; // code, docs, `en` base, languages.json, etc.
}

if (!hasOtherChanges && langs.size > 1) {
  console.error(
    `A translation PR may modify at most one non-English locale, but this one touches: ${[...langs].sort().join(", ")}.\n` +
      "Split each language into its own PR so translations can be reviewed independently.",
  );
  process.exit(1);
}

console.log(
  hasOtherChanges
    ? "OK — core PR (touches code / en / metadata); single-language rule not enforced."
    : langs.size === 1
      ? `OK — one non-English locale changed: ${[...langs][0]}.`
      : "OK — no non-English locale changes.",
);
