import { describe, it, expect } from "vitest";
import { buildIndex, articleTree, findArticle, DEFAULT_LOCALE } from "./content";

const files = {
  "../../content/help/en/recording-audio.md": "---\ntitle: Recording\ngroup: getting-started\norder: 20\n---\nen body",
  "../../content/help/en/what-is-diariz.md": "---\ntitle: What is Diariz\ngroup: getting-started\norder: 10\n---\nintro",
  "../../content/help/en/formulas.md": "---\ntitle: Formulas\ngroup: asking-questions\norder: 10\n---\nformulas body",
  "../../content/help/de/formulas.md": "---\ntitle: Formeln\ngroup: asking-questions\norder: 10\n---\nde body",
};

const index = buildIndex(files);

describe("buildIndex", () => {
  it("derives the slug and locale from the file path", () => {
    const a = findArticle(index, "formulas", "de");
    expect(a?.slug).toBe("formulas");
    expect(a?.locale).toBe("de");
    expect(a?.title).toBe("Formeln");
  });

  it("loads every file it is given", () => {
    expect(index.length).toBe(4);
  });
});

describe("findArticle", () => {
  it("prefers the requested locale", () => {
    expect(findArticle(index, "formulas", "de")?.body).toBe("de body");
  });

  it("falls back to English when the locale has no translation", () => {
    const a = findArticle(index, "recording-audio", "de");
    expect(a?.locale).toBe(DEFAULT_LOCALE);
    expect(a?.body).toBe("en body");
  });

  it("falls back to English for an entirely unknown locale", () => {
    expect(findArticle(index, "formulas", "zz")?.locale).toBe(DEFAULT_LOCALE);
  });

  it("returns undefined for an unknown slug", () => {
    expect(findArticle(index, "no-such-article", "en")).toBeUndefined();
  });
});

describe("articleTree", () => {
  it("groups articles and orders the groups by the declared group order", () => {
    const tree = articleTree(index, "en");
    expect(tree.map((g) => g.id)).toEqual(["getting-started", "asking-questions"]);
  });

  it("sorts articles within a group by their order field", () => {
    const tree = articleTree(index, "en");
    const first = tree.find((g) => g.id === "getting-started");
    expect(first?.articles.map((a) => a.slug)).toEqual(["what-is-diariz", "recording-audio"]);
  });

  it("shows one entry per slug, using the translated article where there is one", () => {
    const tree = articleTree(index, "de");
    const all = tree.flatMap((g) => g.articles);
    expect(all.filter((a) => a.slug === "formulas")).toHaveLength(1);
    expect(all.find((a) => a.slug === "formulas")?.title).toBe("Formeln");
  });

  it("still lists untranslated articles, in English", () => {
    const tree = articleTree(index, "de");
    const all = tree.flatMap((g) => g.articles);
    expect(all.find((a) => a.slug === "recording-audio")?.title).toBe("Recording");
  });

  it("omits groups that have no articles", () => {
    const tree = articleTree(buildIndex({}), "en");
    expect(tree).toEqual([]);
  });
});
