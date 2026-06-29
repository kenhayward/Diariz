import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import i18n from "./lib/i18n";
import { getStoredLanguage, resolveLanguage, setStoredLanguage } from "./lib/language";
import { languageMeta, uiLanguages } from "./lib/uiLanguages";
import type { Language } from "./lib/types";

interface LanguageState {
  /** The active UI language code (always one with a catalog). */
  language: string;
  /** Switch the UI language and persist the choice. */
  setLanguage: (code: string) => void;
  /** The languages offered in the UI picker (those with a shipped catalog). */
  available: Language[];
}

const LanguageContext = createContext<LanguageState | null>(null);

const codes = uiLanguages.map((l) => l.code);

/** Apply a language to i18next and the document (`<html lang>` + `<html dir>` for RTL). */
function apply(code: string) {
  i18n.changeLanguage(code);
  const el = document.documentElement;
  el.lang = code;
  el.dir = languageMeta(code)?.rtl ? "rtl" : "ltr";
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [language, setLanguageState] = useState(() =>
    resolveLanguage(getStoredLanguage(), navigator.languages ?? [navigator.language], codes),
  );

  useEffect(() => apply(language), [language]);

  const setLanguage = useCallback((code: string) => {
    if (!codes.includes(code)) return; // ignore languages without a catalog
    setStoredLanguage(code);
    setLanguageState(code);
  }, []);

  const value = useMemo<LanguageState>(
    () => ({ language, setLanguage, available: uiLanguages }),
    [language, setLanguage],
  );

  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}

export function useLanguage(): LanguageState {
  const ctx = useContext(LanguageContext);
  if (!ctx) throw new Error("useLanguage must be used within LanguageProvider");
  return ctx;
}
