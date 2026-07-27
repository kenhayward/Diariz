import { parseArticle, DEFAULT_LOCALE, type HelpArticle } from "./parseArticle";
import { HELP_GROUPS } from "./groups";
import { buildAssetMap, resolveImages, type HelpAssets } from "./images";

// Re-exported so callers keep importing locale concerns from the loader, not the parser.
export { DEFAULT_LOCALE };

export interface HelpGroup {
  id: string;
  /** i18n key in the `help` namespace. */
  label: string;
  articles: HelpArticle[];
}

const PATH = /\/content\/help\/([^/]+)\/([^/]+)\.md$/;

/// Turn a `path -> raw markdown` map into a flat article list. Split out from the glob below so it can be
/// exercised with fixtures; the real content files are covered by the content gate test instead.
export function buildIndex(files: Record<string, string>): HelpArticle[] {
  const out: HelpArticle[] = [];
  for (const path in files) {
    const m = PATH.exec(path.replace(/\\/g, "/"));
    if (!m) continue;
    out.push(parseArticle(m[2], m[1], files[path]));
  }
  return out;
}

/// The article for `slug` in `locale`, falling back to English when there is no translation. Returns
/// `undefined` for an unknown slug so callers can render a not-found state rather than a blank pane.
export function findArticle(index: HelpArticle[], slug: string, locale: string): HelpArticle | undefined {
  return (
    index.find((a) => a.slug === slug && a.locale === locale) ??
    index.find((a) => a.slug === slug && a.locale === DEFAULT_LOCALE)
  );
}

/// The nav tree for a locale: every distinct slug once, translated where a translation exists, bucketed
/// into `HELP_GROUPS` order and sorted by each article's `order` within its group. Empty groups are
/// dropped so the nav never shows a heading with nothing under it.
export function articleTree(index: HelpArticle[], locale: string): HelpGroup[] {
  const slugs = [...new Set(index.map((a) => a.slug))];
  const resolved = slugs
    .map((s) => findArticle(index, s, locale))
    .filter((a): a is HelpArticle => a !== undefined);

  return HELP_GROUPS.map(({ id, label }) => ({
    id,
    label,
    articles: resolved
      .filter((a) => a.group === id)
      .sort((x, y) => x.order - y.order || x.title.localeCompare(y.title)),
  })).filter((g) => g.articles.length > 0);
}

// The real content. Eager + `?raw` mirrors how `lib/i18n.ts` discovers locale catalogs, so adding an
// article is a file drop with no code change. The content lives under `apps/web/` because the web
// Dockerfile's build context is that directory - the repo-root `docs/` folder is not visible to it.
const modules = import.meta.glob("../../content/help/*/*.md", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

export const HELP_INDEX: HelpArticle[] = buildIndex(modules);

// Screenshots co-located with the articles. Without `query: "?url"` Vite would try to *parse* the image;
// this asks for the emitted, fingerprinted URL instead, which `resolveImages` swaps into the markdown.
const assetModules = import.meta.glob("../../content/help/*/**/*.{png,jpg,jpeg,gif,svg,webp}", {
  eager: true,
  query: "?url",
  import: "default",
}) as Record<string, string>;

export const HELP_ASSETS: HelpAssets = buildAssetMap(assetModules);

/// An article's body with its relative image paths rewritten to bundled URLs, ready to render.
export function articleBody(article: HelpArticle): string {
  return resolveImages(article.body, article.locale, HELP_ASSETS);
}

/// Every slug that has an article, for the content gate's coverage check.
export const HELP_SLUGS: string[] = [...new Set(HELP_INDEX.map((a) => a.slug))].sort();
