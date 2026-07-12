import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AdminUser } from "../lib/types";

/// A "search to add" type-ahead for group membership, modeled on SpeakerAssign: an always-visible input
/// whose results dropdown appears only once the user types (a flat list of every user doesn't scale past
/// ~100). Matches are a case-insensitive "contains" over name AND email, excluding users already in the
/// group. Selecting a match calls onAdd and clears the box. The results popover closes on outside-click or
/// Escape - same convention as SpeakerAssign / KebabMenu.
export default function AddMemberTypeahead({
  users,
  excludeIds,
  onAdd,
  label,
}: {
  users: AdminUser[];
  excludeIds: string[];
  onAdd: (userId: string) => void;
  label?: string;
}) {
  const { t } = useTranslation("admin");
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
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

  const exclude = new Set(excludeIds);
  const q = query.trim().toLowerCase();
  const matches = q
    ? users.filter(
        (u) =>
          !exclude.has(u.id) &&
          ((u.fullName ?? "").toLowerCase().includes(q) || u.email.toLowerCase().includes(q)),
      )
    : [];

  function choose(userId: string) {
    onAdd(userId);
    setQuery("");
    setOpen(false);
  }

  const optionClass =
    "block w-full px-3 py-1.5 text-left text-xs hover:bg-gray-50 dark:hover:bg-gray-800 text-gray-700 dark:text-gray-200";

  return (
    <div className="relative" ref={ref}>
      <input
        role="combobox"
        aria-expanded={open && q !== ""}
        aria-label={label ?? t("addMemberLabel")}
        value={query}
        placeholder={t("addMemberPlaceholder")}
        onChange={(e) => {
          setQuery(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        className="w-full rounded border px-2 py-1 text-sm outline-none dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      {open && q !== "" && (
        <div className="absolute left-0 z-20 mt-1 w-full overflow-hidden rounded-lg border bg-white shadow-lg dark:border-gray-700 dark:bg-gray-900">
          {matches.length === 0 ? (
            <p className="px-3 py-1.5 text-xs text-gray-400 dark:text-gray-500">{t("noMemberMatches")}</p>
          ) : (
            <div className="max-h-48 overflow-y-auto py-1">
              {matches.map((u) => (
                <button key={u.id} type="button" role="option" onClick={() => choose(u.id)} className={optionClass}>
                  <span>{u.fullName || u.email}</span>
                  {u.fullName && <span className="ml-1.5 text-gray-400 dark:text-gray-500">{u.email}</span>}
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
