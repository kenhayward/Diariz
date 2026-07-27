/// One help article: the parsed form of a single `content/help/<locale>/<slug>.md` file.
///
/// `summary` is deliberately part of the article rather than a separate snippet file - the contextual
/// `?` popover renders exactly this string, so the short and long forms of an explanation cannot drift
/// apart. Keep it to a couple of sentences (the content gate enforces a length cap).
export interface HelpArticle {
  slug: string;
  locale: string;
  title: string;
  summary: string;
  group: string;
  order: number;
  body: string;
}

/// Articles that forget to declare a group still have to land somewhere in the nav tree.
export const FALLBACK_GROUP = "other";

/// Article prose is authored in English; other locales are optional overlays that fall back to `en`.
/// Lives here, in the module with no imports of its own, so both the loader and the image resolver can
/// use it without forming an import cycle.
export const DEFAULT_LOCALE = "en";

/// Articles with no explicit `order` sort after the ones that have it.
const DEFAULT_ORDER = 999;

const FRONT_MATTER = /^---\n([\s\S]*?)\n---\n?/;

/// Parse a raw markdown file into a `HelpArticle`.
///
/// The front-matter block is intentionally *not* YAML - it only supports `key: value` scalar lines, so
/// there is no parser dependency and no way for content to grow structure the loader does not understand.
/// A file with no front matter is still usable: it falls back to the slug as its title.
export function parseArticle(slug: string, locale: string, raw: string): HelpArticle {
  // Normalise CRLF up front so the front-matter regex and the body are line-ending agnostic - a content
  // file authored on Windows must parse identically on Linux CI.
  const text = raw.replace(/\r\n/g, "\n");
  const match = FRONT_MATTER.exec(text);
  const fields = match ? parseFields(match[1]) : {};
  // Drop the blank line authors conventionally leave between the block and the first heading, so the
  // body always starts at real content.
  const body = match ? text.slice(match[0].length).replace(/^\n+/, "") : text;
  const order = Number(fields.order);

  return {
    slug,
    locale,
    title: fields.title || slug,
    summary: fields.summary ?? "",
    group: fields.group || FALLBACK_GROUP,
    order: Number.isFinite(order) ? order : DEFAULT_ORDER,
    body,
  };
}

/// `key: value` lines only. Blank lines and `#` comments are skipped; the value keeps any further
/// colons (so a title like "Chat: ask anything" survives).
function parseFields(block: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const line of block.split("\n")) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;
    const colon = trimmed.indexOf(":");
    if (colon === -1) continue;
    out[trimmed.slice(0, colon).trim()] = trimmed.slice(colon + 1).trim();
  }
  return out;
}
