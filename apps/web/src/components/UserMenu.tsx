import { useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { useTheme } from "../theme";
import type { ThemeChoice } from "../lib/theme";
import Avatar from "./Avatar";
import SettingsModal from "./SettingsModal";

const THEMES: { value: ThemeChoice; label: string }[] = [
  { value: "auto", label: "Auto" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

export default function UserMenu() {
  const { initials, email, logout } = useAuth();
  const { theme, setTheme } = useTheme();
  const [open, setOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-label="Account"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <Avatar initials={initials} />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 z-20 mt-2 w-48 overflow-hidden rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {email && (
            <div className="truncate px-3 py-2 text-xs text-gray-500 dark:text-gray-400">{email}</div>
          )}
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              setSettingsOpen(true);
            }}
            className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Settings
          </button>

          <div className="border-t py-1 dark:border-gray-700">
            <div className="px-3 py-1 text-xs font-medium text-gray-400 dark:text-gray-500">Theme</div>
            {THEMES.map((t) => (
              <button
                key={t.value}
                type="button"
                role="menuitemradio"
                aria-checked={theme === t.value}
                onClick={() => setTheme(t.value)}
                className="flex w-full items-center justify-between px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                {t.label}
                {theme === t.value && <span aria-hidden>✓</span>}
              </button>
            ))}
          </div>

          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              logout();
            }}
            className="block w-full border-t px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Sign Out
          </button>
        </div>
      )}

      {settingsOpen && <SettingsModal onClose={() => setSettingsOpen(false)} />}
    </div>
  );
}
