// The user's chosen UI language, persisted locally so it survives reloads and is available before
// sign-in (mirrors theme.ts). The authoritative per-user value lives in their profile; this is the
// client-side cache + the pre-login signup choice. Phase 4 wires it into the actual i18n framework.

const LANGUAGE_KEY = "diariz.language";

/** The stored UI-language code (BCP-47), or null when the user hasn't chosen one. */
export function getStoredLanguage(): string | null {
  const v = localStorage.getItem(LANGUAGE_KEY);
  return v && v.trim() ? v : null;
}

/** Persist (or, with null, clear) the chosen UI-language code. */
export function setStoredLanguage(code: string | null): void {
  if (code && code.trim()) localStorage.setItem(LANGUAGE_KEY, code);
  else localStorage.removeItem(LANGUAGE_KEY);
}
