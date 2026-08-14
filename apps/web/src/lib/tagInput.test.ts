import { describe, expect, it } from "vitest";
import { addTag, normalizeTag } from "./tagInput";

/// U+00A0. Built from its code point rather than written as a literal or an escape: an invisible character in
/// a source literal is unreviewable, and the C# mirror does the same.
const NBSP = String.fromCharCode(0xa0);
const A62 = "a".repeat(62);
const A63 = "a".repeat(63);
const A64 = "a".repeat(64);

/// The normalisation contract, as a table shared VERBATIM with the C# mirror: the same input/expected pairs
/// live in `SharedFixture` in `tests/Diariz.Api.Tests/TagTextTests.cs`. The two implementations
/// (`normalizeTag` here and `TagText.Normalize` there) mirror each other by convention only - nothing
/// compiles them together - and a drift between them surfaces to the user as a chip that re-spells itself
/// after the refetch, or as a 500 from a duplicate insert. Keeping one table on each side means a future
/// divergence fails a test instead. Add a case to BOTH lists or to neither.
///
/// Two known asymmetries are deliberately absent, because the two regex engines genuinely disagree and
/// neither behaviour is worth changing:
///   * U+0085 (NEL) - .NET's `\s` matches it, JavaScript's does not.
///   * U+FEFF (BOM / zero-width no-break space) - JavaScript's `\s` matches it, .NET's does not.
/// Both are accepted, documented divergences. A null input is also absent: `TagText.Normalize` takes a
/// nullable string while this one takes a `string`, which is a signature difference, not a behaviour one.
const SHARED_FIXTURE: [string, string | null][] = [
  // Plain words, case preserved either way.
  ["metadata", "metadata"],
  ["  metadata  ", "metadata"],
  ["Roadmap", "Roadmap"],
  ["iOS", "iOS"],

  // Whitespace inside becomes a hyphen, so a phrase lands as one token.
  ["Data Collection", "Data-Collection"],
  ["budget planning 2026", "budget-planning-2026"],
  ["many   spaces", "many-spaces"],
  ["spaced\tout\nword", "spaced-out-word"],
  ["line\r\nbreak", "line-break"],
  [`${NBSP}nbsp${NBSP}`, "nbsp"], // a non-breaking space (pasted from a doc) is whitespace to both

  // Edge hyphens go, however many.
  ["-leading", "leading"],
  ["trailing-", "trailing"],
  ["--both--", "both"],

  // Nothing usable left.
  ["", null],
  ["   ", null],
  ["\t\n ", null],
  ["-", null],
  ["---", null],

  // Truncation to the column length - and the hyphen trim has to run AGAIN after the slice, or the result is
  // not something normalizeTag would leave alone. A tag whose 64th character ends up a hyphen used to keep
  // it, which made the function non-idempotent: adopting an over-long suggestion inserted a second row and
  // the chip visibly re-spelled itself after the refetch, and re-adding such a tag missed the existing row,
  // inserted, and tripped the server's unique index as an unhandled 500.
  ["x".repeat(100), "x".repeat(64)],
  [`${A64}bbb`, A64],
  [`${A63}-bbbb`, A63], // hyphen exactly at index 63
  [`${A63} bbbb`, A63], // whitespace at index 63 -> hyphen -> trimmed
  [`${A63}-`, A63],
  [`${A62}--bbb`, A62], // more than one hyphen at the boundary
];

describe("normalizeTag", () => {
  it.each(SHARED_FIXTURE)("normalises %j to %j", (raw, expected) => {
    expect(normalizeTag(raw)).toBe(expected);
  });

  /// The property behind the truncation cases above: a normalised tag is already normalised. The server
  /// normalises again on every lookup, so a value normalizeTag would change again can never be found by the
  /// text it was stored as.
  it.each(SHARED_FIXTURE)("is idempotent on %j", (raw, expected) => {
    const once = normalizeTag(raw);
    expect(once).toBe(expected);
    if (once !== null) expect(normalizeTag(once)).toBe(once);
  });
});

describe("addTag", () => {
  it("appends a new tag and reports what it added", () => {
    expect(addTag(["one"], "two")).toEqual({ tags: ["one", "two"], added: "two" });
  });

  it("normalises before adding", () => {
    expect(addTag([], "budget planning")).toEqual({
      tags: ["budget-planning"],
      added: "budget-planning",
    });
  });

  it("rejects a case-insensitive duplicate without changing the list", () => {
    const list = ["metadata"];
    const result = addTag(list, "METADATA");
    expect(result).toEqual({ tags: ["metadata"], added: null });
    expect(result.tags).toEqual(list);
  });

  it("rejects unusable text", () => {
    expect(addTag(["one"], "  ")).toEqual({ tags: ["one"], added: null });
  });

  it("does not mutate the list it was given", () => {
    const list = ["one"];
    addTag(list, "two");
    expect(list).toEqual(["one"]);
  });
});
