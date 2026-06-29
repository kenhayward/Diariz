import { api } from "./api";
import type { Language } from "./types";

// The supported-language list is static reference data served by the API. Fetch it once and reuse the
// cached promise (so concurrent callers share a single request).
let cache: Promise<Language[]> | null = null;

export function fetchLanguages(): Promise<Language[]> {
  cache ??= api.getLanguages();
  return cache;
}

/// Reset the cache (tests).
export function _resetLanguageCache(): void {
  cache = null;
}
