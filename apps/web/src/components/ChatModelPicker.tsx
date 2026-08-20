import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ChatModelOption } from "../lib/types";

interface Props {
  models: ChatModelOption[];
  selectedId: string | null;
  /// True while a reply is streaming: switching then would change the model behind an answer already
  /// arriving, and the turn is in flight with the old one either way.
  disabled?: boolean;
  onSelect: (id: string) => void;
}

/// Chooses which model answers the next chat turn.
///
/// Shown even when there is only one model to pick, so the affordance is discoverable and the toolbar's
/// layout does not shift as an administrator adds or removes models. Rows carry the LABEL, never the slug:
/// the slug is what the endpoint needs, not what someone choosing a model should have to read.
export default function ChatModelPicker({ models, selectedId, disabled = false, onSelect }: Props) {
  const { t } = useTranslation("chat");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDocument(e: MouseEvent) {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDocument);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocument);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  // An administrator can un-tick a model between a conversation being saved and reopened, so a stored id
  // may name a model that is no longer in the list. Falling back to the default is the same rule the
  // server applies to the turn itself, which keeps the two in agreement.
  const selected = models.find((m) => m.id === selectedId) ?? models.find((m) => m.isDefault) ?? null;

  return (
    <div ref={ref} className="relative flex items-center">
      <button
        type="button"
        aria-label={t("modelPicker", { model: selected?.label ?? "" })}
        title={t("modelPicker", { model: selected?.label ?? "" })}
        aria-expanded={open}
        disabled={disabled}
        onClick={() => !disabled && setOpen((v) => !v)}
        className="rounded p-1 text-indigo-500 transition-colors hover:bg-gray-100 hover:text-indigo-600 disabled:opacity-40 dark:text-indigo-400 dark:hover:bg-gray-700"
      >
        <SparkleIcon />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-30 mt-1 max-h-64 w-72 max-w-[calc(100vw-2rem)] overflow-y-auto overflow-x-hidden rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
        >
          {models.map((m) => (
            <button
              key={m.id}
              type="button"
              role="menuitemradio"
              aria-checked={m.id === (selected?.id ?? null)}
              onClick={() => {
                onSelect(m.id);
                setOpen(false);
              }}
              // A FLEX row, and the label a flex item with min-w-0. `truncate` on the inline span it used
              // to be contributed only its `white-space: nowrap` - overflow and text-overflow do not apply
              // to a non-replaced inline box - so a long imported slug could neither wrap nor ellipsise:
              // it overflowed the menu (giving it a horizontal scrollbar, since overflow-y:auto makes
              // overflow-x compute to auto) and pushed the context length onto a second line, where it was
              // off the right-hand edge and invisible. Measured before and after in a browser: 421px of
              // scroll width in a 241px box, and rows at 54px instead of 33px.
              // min-w-0 is load-bearing: without it a flex item refuses to shrink below its content.
              title={m.label}
              className={`flex w-full items-baseline gap-1.5 px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-700 ${
                m.id === selected?.id ? "font-medium text-indigo-600 dark:text-indigo-400" : ""
              }`}
            >
              <span className="min-w-0 truncate">{m.label}</span>
              <span className="shrink-0 text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
                ({m.contextLength.toLocaleString()} {t("ctxSuffix")})
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/// A four-point sparkle. Inline like ChatPanel's other toolbar icons rather than a dependency, and filled
/// rather than stroked so it still reads at 16px.
const SparkleIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M12 2l2.2 6.1L20.4 10l-6.2 1.9L12 18l-2.2-6.1L3.6 10l6.2-1.9L12 2z" />
    <path d="M18.5 15l.9 2.5 2.6.8-2.6.8-.9 2.5-.9-2.5-2.6-.8 2.6-.8.9-2.5z" opacity="0.7" />
  </svg>
);
