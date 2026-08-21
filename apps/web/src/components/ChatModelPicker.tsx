import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import type { ChatModelOption } from "../lib/types";

/// Menu width in px. Wide enough for an imported slug plus its context length.
const WIDTH = 288;
/// Gap between the sparkle button and the menu.
const GAP = 4;
/// Keeps the menu off the very edge of the viewport when it has to be nudged back inside.
const MARGIN = 8;

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
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  // Right-aligned on the button, nudged back inside the viewport rather than allowed to hang off it.
  const place = useCallback(() => {
    const anchor = buttonRef.current?.getBoundingClientRect();
    if (!anchor) return;
    setPos({
      top: anchor.bottom + GAP,
      left: Math.max(MARGIN, Math.min(anchor.right - WIDTH, window.innerWidth - WIDTH - MARGIN)),
    });
  }, []);

  useLayoutEffect(() => {
    if (open) place();
  }, [open, place]);

  useEffect(() => {
    if (!open) return;
    function onDocument(e: MouseEvent) {
      // The menu is no longer a descendant of the picker, so it needs its own containment check -
      // without it a row's mousedown would close the menu and unmount the row before its click landed.
      const target = e.target as Node;
      if (!buttonRef.current?.contains(target) && !menuRef.current?.contains(target)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDocument);
    document.addEventListener("keydown", onKey);
    // A fixed menu does not travel with its anchor: resizing the window moves the right-docked panel,
    // and any scroll under it shifts the button. Re-place rather than let the menu drift off it.
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      document.removeEventListener("mousedown", onDocument);
      document.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open, place]);

  // An administrator can un-tick a model between a conversation being saved and reopened, so a stored id
  // may name a model that is no longer in the list. Falling back to the default is the same rule the
  // server applies to the turn itself, which keeps the two in agreement.
  const selected = models.find((m) => m.id === selectedId) ?? models.find((m) => m.isDefault) ?? null;

  return (
    // No `relative` any more: the menu is portalled, so nothing here is positioned against this box.
    <div className="flex items-center">
      <button
        ref={buttonRef}
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

      {open &&
        createPortal(
          // Portalled to the body and positioned FIXED, because the picker sits in a chat panel only
          // 260-640px wide whose scroll container computes `overflow-x: auto`. A menu laid out inside
          // that subtree hung 77px off its left edge at the default 320px width (measured: menu left
          // 200.7 against a container starting at 278), and overflow to the LEFT of a scroll box is
          // unreachable - scrollWidth equalled clientWidth - so the model names were simply invisible
          // while the context lengths beside them read fine. The old `max-w-[calc(100vw-2rem)]` clamped
          // against the viewport, never the panel, so it never engaged. Escaping the subtree also lets a
          // long name use the width it needs instead of the width the panel happens to have.
          <div
            ref={menuRef}
            role="menu"
            style={{ width: WIDTH, top: pos?.top ?? 0, left: pos?.left ?? 0, visibility: pos ? "visible" : "hidden" }}
            className="fixed z-30 max-h-64 overflow-y-auto overflow-x-hidden rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
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
                // A FLEX row, and the label a flex item with min-w-0. `truncate` on the inline span it
                // used to be contributed only its `white-space: nowrap` - overflow and text-overflow do
                // not apply to a non-replaced inline box - so a long imported slug could neither wrap nor
                // ellipsise: it overflowed the menu (giving it a horizontal scrollbar, since
                // overflow-y:auto makes overflow-x compute to auto) and pushed the context length onto a
                // second line, where it was off the right-hand edge and invisible. Measured before and
                // after in a browser: 421px of scroll width in a 241px box, and rows at 54px instead of
                // 33px. min-w-0 is load-bearing: without it a flex item refuses to shrink below its
                // content.
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
          </div>,
          document.body,
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
