import type { HelpArticle } from "./parseArticle";

export interface HelpHit {
  article: HelpArticle;
  score: number;
  /** A short piece of context to show under the title in the results list. */
  snippet: string;
}

/// Where a match was found, and what it is worth. A title hit is the strongest signal that an article is
/// *about* the query; a stray body mention is the weakest.
const TITLE_WEIGHT = 10;
const SUMMARY_WEIGHT = 5;
const HEADING_WEIGHT = 3;
const BODY_WEIGHT = 1;

/** Characters of body text to show either side of a match. */
const SNIPPET_PAD = 60;

/// Rank help articles against a free-text query.
///
/// Deliberately a plain scored substring search rather than a fuzzy-search dependency: the corpus is a
/// dozen short articles, so anything more is weight the bundle does not need to carry. Pure and
/// synchronous, so the page can call it straight from a controlled input without debouncing.
export function searchArticles(query: string, articles: HelpArticle[]): HelpHit[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return [];

  const hits: HelpHit[] = [];
  for (const a of articles) {
    const body = a.body.toLowerCase();
    const bodyAt = body.indexOf(needle);

    let score = 0;
    if (a.title.toLowerCase().includes(needle)) score += TITLE_WEIGHT;
    if (a.summary.toLowerCase().includes(needle)) score += SUMMARY_WEIGHT;
    if (headings(a.body).some((h) => h.includes(needle))) score += HEADING_WEIGHT;
    if (bodyAt !== -1) score += BODY_WEIGHT;
    if (score === 0) continue;

    hits.push({ article: a, score, snippet: snippetFor(a, bodyAt) });
  }

  // Strongest first; ties resolve alphabetically so the order is stable rather than glob-dependent.
  return hits.sort((x, y) => y.score - x.score || x.article.title.localeCompare(y.article.title));
}

/// Heading text with the leading `#`s stripped, lowercased for comparison.
function headings(body: string): string[] {
  return body
    .split("\n")
    .filter((l) => l.startsWith("#"))
    .map((l) => l.replace(/^#+\s*/, "").toLowerCase());
}

/// A window of body text around the match, ellipsised at each end that was cut. Falls back to the
/// summary when the match was in the metadata rather than the body.
function snippetFor(a: HelpArticle, bodyAt: number): string {
  if (bodyAt === -1) return a.summary;
  const start = Math.max(0, bodyAt - SNIPPET_PAD);
  const end = Math.min(a.body.length, bodyAt + SNIPPET_PAD);
  const text = a.body.slice(start, end).replace(/\s+/g, " ").trim();
  return `${start > 0 ? "..." : ""}${text}${end < a.body.length ? "..." : ""}`;
}
