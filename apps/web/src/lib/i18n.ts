import i18next from "i18next";
import { initReactI18next } from "react-i18next";

// Auto-discover every locale catalog: dropping a new `locales/<lang>/<namespace>.json` file registers
// it with NO code change here. `eager` makes the import synchronous so resources are ready before render.
const modules = import.meta.glob("../locales/*/*.json", { eager: true }) as Record<string, { default: object }>;

const resources: Record<string, Record<string, object>> = {};
const locales = new Set<string>();
const namespaces = new Set<string>();
for (const path in modules) {
  const m = /\/locales\/([^/]+)\/([^/]+)\.json$/.exec(path);
  if (!m) continue;
  const [, lng, ns] = m;
  locales.add(lng);
  namespaces.add(ns);
  (resources[lng] ??= {})[ns] = modules[path].default;
}

/** The language codes that have a shipped catalog folder (e.g. ["en", "es", "fr", "de"]). */
export const discoveredLocales = [...locales];

i18next.use(initReactI18next).init({
  resources,
  lng: "en",
  fallbackLng: "en", // a missing key in any locale falls back to English (defence in depth)
  defaultNS: "common",
  ns: [...namespaces],
  interpolation: { escapeValue: false }, // React already escapes
  react: { useSuspense: false },
});

export default i18next;
