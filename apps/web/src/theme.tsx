import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { applyTheme, getStoredTheme, setStoredTheme, type ThemeChoice } from "./lib/theme";

interface ThemeState {
  theme: ThemeChoice;
  setTheme: (choice: ThemeChoice) => void;
}

const ThemeContext = createContext<ThemeState | null>(null);

function prefersDark(): boolean {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<ThemeChoice>(() => getStoredTheme());

  // Apply on change, and re-apply on OS preference change while in "auto".
  useEffect(() => {
    applyTheme(theme, document.documentElement, prefersDark());
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => applyTheme(theme, document.documentElement, mq.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, [theme]);

  // Stable identity: ThemeSync's effect depends on setTheme, so a fresh function each render would make it
  // re-run and re-apply the server profile - reverting a just-picked (unsaved) choice back to the stored value.
  const setTheme = useCallback((choice: ThemeChoice) => {
    setStoredTheme(choice);
    setThemeState(choice);
  }, []);

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeState {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used within ThemeProvider");
  return ctx;
}
