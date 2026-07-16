import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { SpeakerProfile } from "../lib/types";

/// Typeahead replacement for the speaker-assignment dropdown. The trigger shows the current assignment;
/// opening it reveals an input. The people list appears only once the user types (there are now too many
/// enrolled people for a flat list), filtered by a case-insensitive "contains". A typed name with no exact
/// match offers a "Create" row (enrol a new person). "Multiple speakers" and "Unassign" are always
/// reachable in a footer. Closes on outside-click or Escape — same popover behaviour as KebabMenu.
///
/// Handlers may return a promise: the popover then stays open showing a spinner until it settles, because
/// enrolling a new person round-trips the API and refetches the (large) people list, which takes seconds.
export default function SpeakerAssign({
  label,
  profiles,
  profileId,
  isMulti,
  displayName,
  width = "w-64",
  subtle = false,
  onAssign,
  onCreate,
  onMulti,
}: {
  label: string;
  profiles: SpeakerProfile[];
  profileId: string | null;
  isMulti: boolean;
  /// Shown on the trigger when no profile is assigned. Defaults to "Unassigned"; the transcript passes the
  /// segment's speaker name so a row keeps reading as it did before the dropdown replaced the plain label.
  displayName?: string;
  /// Tailwind width of the whole control (the popover is at least this wide).
  width?: string;
  /// Render the trigger as plain text (no field border) until hovered — for use inline in a transcript row.
  subtle?: boolean;
  onAssign: (profileId: string | null) => void | Promise<void>;
  onCreate: (name: string) => void | Promise<void>;
  onMulti: () => void | Promise<void>;
}) {
  const { t } = useTranslation("workspace");
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [busy, setBusy] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open || busy) return; // don't let a stray click/Escape close the popover mid-write
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
  }, [open, busy]);

  const current = isMulti
    ? t("multipleSpeakers")
    : (profiles.find((p) => p.id === profileId)?.name ?? displayName ?? t("unassigned"));

  const q = query.trim();
  const matches = q ? profiles.filter((p) => p.name.toLowerCase().includes(q.toLowerCase())) : [];
  const hasExact = profiles.some((p) => p.name.toLowerCase() === q.toLowerCase());

  async function choose(fn: () => void | Promise<void>) {
    const pending = fn();
    if (pending) {
      setBusy(true);
      try {
        await pending;
      } finally {
        setBusy(false);
      }
    }
    setOpen(false);
    setQuery("");
  }

  const optionClass =
    "block w-full px-3 py-1.5 text-left text-xs hover:bg-gray-50 dark:hover:bg-gray-800 text-gray-700 dark:text-gray-200";

  return (
    // Fixed, generous width so any speaker name fits and the following row items line up across speakers.
    <div className={`relative ${width}`} ref={ref}>
      <button
        type="button"
        aria-label={t("assignAria", { label })}
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={busy}
        onClick={() => setOpen((v) => !v)}
        className={
          subtle
            ? "flex w-full items-center justify-between rounded px-1 py-0.5 text-left text-sm font-medium text-gray-700 hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-700"
            : "flex w-full items-center justify-between rounded border px-2 py-1 text-xs text-gray-700 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        }
      >
        <span className="truncate">{current}</span>
        <span aria-hidden className="ml-1 text-gray-400">▾</span>
      </button>
      {open && busy && (
        <div
          role="status"
          aria-label={t("assignSaving")}
          className="absolute left-0 z-20 mt-1 flex w-full min-w-48 items-center gap-2 rounded-lg border bg-white px-3 py-2 text-xs text-gray-500 shadow-lg dark:border-gray-700 dark:bg-gray-900 dark:text-gray-400"
        >
          <span
            aria-hidden
            className="h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-gray-300 border-t-blue-500 dark:border-gray-600 dark:border-t-blue-400"
          />
          {t("assignSaving")}
        </div>
      )}
      {open && !busy && (
        <div className="absolute left-0 z-20 mt-1 w-full min-w-48 overflow-hidden rounded-lg border bg-white shadow-lg dark:border-gray-700 dark:bg-gray-900">
          <input
            autoFocus
            role="combobox"
            aria-expanded
            aria-label={t("assignAria", { label })}
            value={query}
            placeholder={t("assignTypeaheadPlaceholder")}
            onChange={(e) => setQuery(e.target.value)}
            className="w-full border-b px-3 py-1.5 text-xs outline-none dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          {q && (
            <div className="max-h-48 overflow-y-auto py-1">
              {matches.map((p) => (
                <button key={p.id} type="button" role="option" onClick={() => choose(() => onAssign(p.id))} className={optionClass}>
                  {p.name}
                </button>
              ))}
              {!hasExact && (
                <button type="button" role="option" onClick={() => choose(() => onCreate(q))} className={`${optionClass} text-blue-700 dark:text-blue-300`}>
                  {t("createPerson", { name: q })}
                </button>
              )}
            </div>
          )}
          <div className="border-t py-1 dark:border-gray-700">
            <button type="button" role="option" onClick={() => choose(onMulti)} className={optionClass}>
              {t("multipleSpeakers")}
            </button>
            <button type="button" role="option" onClick={() => choose(() => onAssign(null))} className={optionClass}>
              {t("unassigned")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
