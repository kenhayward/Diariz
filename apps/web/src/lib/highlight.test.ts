import { describe, it, expect } from "vitest";
import { highlight, snippetWindow } from "./highlight";

const text = (parts: ReturnType<typeof highlight>) => parts.map((p) => p.text).join("");

describe("highlight", () => {
  it("splits the match out of the surrounding text", () => {
    const parts = highlight("we cut the budget in half", "budget");
    expect(parts.filter((p) => p.match).map((p) => p.text)).toEqual(["budget"]);
    expect(text(parts)).toBe("we cut the budget in half");
  });

  it("matches case-insensitively but keeps the original casing", () => {
    const parts = highlight("The Budget is set", "budget");
    expect(parts.filter((p) => p.match).map((p) => p.text)).toEqual(["Budget"]);
  });

  it("marks every occurrence", () => {
    const parts = highlight("budget, budget, budget", "budget");
    expect(parts.filter((p) => p.match)).toHaveLength(3);
  });

  it("returns the text unmarked when there is no match", () => {
    const parts = highlight("nothing to see", "budget");
    expect(parts.every((p) => !p.match)).toBe(true);
    expect(text(parts)).toBe("nothing to see");
  });

  // The query is user input going into a RegExp - a stray "(" would otherwise throw and blank the panel.
  it("does not throw on regex metacharacters in the query", () => {
    expect(() => highlight("a (b) c", "(")).not.toThrow();
    expect(highlight("a (b) c", "(").filter((p) => p.match).map((p) => p.text)).toEqual(["("]);
    expect(() => highlight("cost is $5 *net*", "$5 *")).not.toThrow();
  });

  it("never loses or duplicates text", () => {
    const s = "budget talk about the budget";
    expect(text(highlight(s, "budget"))).toBe(s);
  });

  it("handles an empty query", () => {
    const parts = highlight("some text", "");
    expect(text(parts)).toBe("some text");
    expect(parts.every((p) => !p.match)).toBe(true);
  });
});

describe("snippetWindow", () => {
  // The bug this exists for: the row shows one truncated line, so a match late in a long segment gets
  // clipped off-screen and the snippet never shows why it matched.
  const long =
    "And what really matters is how smart and general and sample efficient the model is during a session.";

  it("keeps the match visible by windowing around it", () => {
    const out = snippetWindow(long, "sample efficient", 40);
    expect(out).toContain("sample efficient");
    expect(out.length).toBeLessThanOrEqual(40 + 2); // plus the ellipses
  });

  it("marks that text was dropped from the front", () => {
    expect(snippetWindow(long, "sample efficient", 40).startsWith("…")).toBe(true);
  });

  it("does not lead with an ellipsis when the match is already near the start", () => {
    expect(snippetWindow(long, "And what", 40).startsWith("…")).toBe(false);
  });

  it("marks that text was dropped from the end", () => {
    expect(snippetWindow(long, "And what", 40).endsWith("…")).toBe(true);
  });

  it("returns short text unchanged", () => {
    expect(snippetWindow("short one", "one", 40)).toBe("short one");
  });

  it("falls back to the start when the query is not present", () => {
    const out = snippetWindow(long, "nowhere", 30);
    expect(out.startsWith("And what")).toBe(true);
    expect(out.endsWith("…")).toBe(true);
  });

  it("handles an empty query", () => {
    expect(snippetWindow("short", "", 40)).toBe("short");
  });

  it("does not throw on regex metacharacters", () => {
    expect(() => snippetWindow(long, "(", 40)).not.toThrow();
  });
});

describe("snippetWindow word boundaries", () => {
  const long =
    "And what really matters is how smart and general and sample efficient the model is during a session.";

  /// The boundary word must be a *whole* word of the source: it either starts the text or follows a space.
  const startsOnAWord = (out: string, source: string) => {
    const first = out.replace(/^…/, "").split(" ")[0];
    return source.startsWith(first) || source.includes(" " + first);
  };
  const endsOnAWord = (out: string, source: string) => {
    const words = out.replace(/…$/, "").split(" ");
    const last = words[words.length - 1];
    return source.endsWith(last) || source.includes(last + " ");
  };

  // "…w smart and general" - cutting mid-word reads as a rendering bug rather than a trimmed quote.
  it("does not start mid-word", () => {
    const out = snippetWindow(long, "sample efficient", 40);
    expect(startsOnAWord(out, long)).toBe(true);
    expect(out).toContain("sample efficient");
  });

  it("does not end mid-word", () => {
    const out = snippetWindow(long, "And what", 40);
    expect(endsOnAWord(out, long)).toBe(true);
  });

  it("still returns the match even when a word is longer than the window", () => {
    const s = "prefix " + "x".repeat(200) + " supercalifragilistic tail";
    expect(snippetWindow(s, "supercalifragilistic", 30)).toContain("supercalifragilistic");
  });
});
