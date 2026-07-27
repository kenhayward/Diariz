import { describe, it, expect } from "vitest";
import { searchArticles } from "./search";
import type { HelpArticle } from "./parseArticle";

function article(over: Partial<HelpArticle>): HelpArticle {
  return {
    slug: "s",
    locale: "en",
    title: "T",
    summary: "",
    group: "g",
    order: 1,
    body: "",
    ...over,
  };
}

const formulas = article({ slug: "formulas", title: "Formulas", body: "Run a saved prompt." });
const chat = article({ slug: "chat", title: "Chat", summary: "Ask about a formulas run.", body: "" });
const actions = article({ slug: "actions", title: "Action items", body: "## Formulas\n\nnothing here" });

describe("searchArticles", () => {
  it("returns nothing for a blank query", () => {
    expect(searchArticles("   ", [formulas, chat])).toEqual([]);
  });

  it("drops articles that do not match at all", () => {
    const hits = searchArticles("formulas", [formulas, chat, article({ slug: "z", title: "Rooms" })]);
    expect(hits.map((h) => h.article.slug)).not.toContain("z");
  });

  it("ranks a title match above a summary match, and a summary above a heading", () => {
    const hits = searchArticles("formulas", [actions, chat, formulas]);
    expect(hits.map((h) => h.article.slug)).toEqual(["formulas", "chat", "actions"]);
  });

  it("is case insensitive", () => {
    expect(searchArticles("FORMULAS", [formulas])).toHaveLength(1);
  });

  it("matches body text", () => {
    const hits = searchArticles("saved prompt", [formulas]);
    expect(hits).toHaveLength(1);
  });

  it("breaks score ties by title", () => {
    const b = article({ slug: "b", title: "Beta rooms" });
    const a = article({ slug: "a", title: "Alpha rooms" });
    expect(searchArticles("rooms", [b, a]).map((h) => h.article.slug)).toEqual(["a", "b"]);
  });

  it("returns a snippet around the body match", () => {
    const long = article({ body: `${"x".repeat(200)} needle ${"y".repeat(200)}` });
    const [hit] = searchArticles("needle", [long]);
    expect(hit.snippet).toContain("needle");
    expect(hit.snippet.length).toBeLessThan(200);
  });

  it("uses the summary as the snippet when the body does not match", () => {
    const [hit] = searchArticles("formulas", [chat]);
    expect(hit.snippet).toBe("Ask about a formulas run.");
  });

  it("ignores markdown heading marks when matching a heading", () => {
    const hits = searchArticles("formulas", [actions]);
    expect(hits[0].article.slug).toBe("actions");
  });
});
