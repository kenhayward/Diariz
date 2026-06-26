export type ThemeChoice = "auto" | "light" | "dark";

const THEME_KEY = "diariz.theme";

export function getStoredTheme(): ThemeChoice {
  const v = localStorage.getItem(THEME_KEY);
  return v === "light" || v === "dark" || v === "auto" ? v : "auto";
}

export function setStoredTheme(choice: ThemeChoice): void {
  localStorage.setItem(THEME_KEY, choice);
}

/// Resolve a user choice + the OS preference into the concrete theme to apply.
export function resolveTheme(choice: ThemeChoice, prefersDark: boolean): "light" | "dark" {
  if (choice === "auto") return prefersDark ? "dark" : "light";
  return choice;
}

/// Toggle the `.dark` class on the given root element (pass document.documentElement at runtime).
export function applyTheme(choice: ThemeChoice, root: HTMLElement, prefersDark: boolean): void {
  root.classList.toggle("dark", resolveTheme(choice, prefersDark) === "dark");
}
