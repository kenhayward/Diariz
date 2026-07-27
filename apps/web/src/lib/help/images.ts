import { DEFAULT_LOCALE } from "./parseArticle";

/// Screenshots live **next to the article that uses them**, e.g.
/// `content/help/en/images/formula-editor.png`, referenced from the markdown as
/// `![Editor](./images/formula-editor.png)`. That keeps an article and its pictures together, so moving
/// or deleting one is a single operation.
///
/// The path in the source is not the path in the build - Vite fingerprints assets - so the raw markdown
/// is rewritten to the emitted URL before rendering.
export type HelpAssets = Record<string, string>;

const ASSET_PATH = /\/content\/help\/(.+)$/;

/// Markdown image syntax, capturing the path and any trailing title:
/// `![alt](path "title")`. The leading `!` is what separates an image from an ordinary link.
const IMAGE = /(!\[[^\]]*\]\()\s*([^)\s]+)((?:\s+"[^"]*")?\s*\))/g;

/// A path we should try to resolve against the bundled assets: not absolute (`/logo.png`, which is a
/// `public/` file), not a full URL, and not a fragment.
function isLocal(path: string): boolean {
  return !/^(?:[a-z][a-z0-9+.-]*:|\/\/|\/|#)/i.test(path);
}

/// Strip the `./` authors naturally write, so `./images/x.png` and `images/x.png` are the same key.
function normalise(path: string): string {
  return path.replace(/^\.\//, "");
}

/// Turn Vite's `path -> emitted URL` glob result into a map keyed by `<locale>/<path within the locale
/// folder>`, which is what an article's relative reference resolves to.
export function buildAssetMap(modules: Record<string, string>): HelpAssets {
  const out: HelpAssets = {};
  for (const path in modules) {
    const m = ASSET_PATH.exec(path.replace(/\\/g, "/"));
    if (m) out[m[1]] = modules[path];
  }
  return out;
}

/// Every repo-local image an article references, as written (normalised). Used by the content gate to
/// fail the build on a screenshot that was renamed or never committed.
export function localImageRefs(body: string): string[] {
  const out: string[] = [];
  for (const m of body.matchAll(IMAGE)) {
    if (isLocal(m[2])) out.push(normalise(m[2]));
  }
  return out;
}

/// Rewrite an article's relative image paths to their bundled URLs.
///
/// A localised article gets its own screenshot when one exists, and otherwise falls back to the English
/// one - a translated page with the English screenshot is far better than a broken image. An
/// unresolvable path is left untouched rather than blanked, so the content gate can name it.
export function resolveImages(body: string, locale: string, assets: HelpAssets): string {
  return body.replace(IMAGE, (whole, open: string, path: string, close: string) => {
    if (!isLocal(path)) return whole;
    const rel = normalise(path);
    const url = assets[`${locale}/${rel}`] ?? assets[`${DEFAULT_LOCALE}/${rel}`];
    return url ? `${open}${url}${close}` : whole;
  });
}
