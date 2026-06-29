import { discoveredLocales } from "./i18n";
import metadata from "../locales/languages.json";
import type { Language } from "./types";

const all = metadata as Language[];

/**
 * The languages available for the *UI* picker: those that have a shipped catalog folder, intersected with
 * the metadata file (so a catalog with no metadata row is ignored). Adding a translated UI language is a
 * data-only change — drop a `locales/xx/` folder and add an `xx` row to `languages.json`.
 */
export const uiLanguages: Language[] = all.filter((l) => discoveredLocales.includes(l.code));

/** Metadata (name + RTL flag) for a language code, or undefined when unknown. */
export function languageMeta(code: string | null | undefined): Language | undefined {
  return code ? all.find((l) => l.code === code) : undefined;
}
