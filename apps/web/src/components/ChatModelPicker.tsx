import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import type { ChatModelOption } from "../lib/types";

/// Menu width in px. Wider than the 320px chat panel on purpose: the menu is portalled and fixed, so it
/// overhangs onto the workspace behind it, which is what gives the name, the description and the context
/// chip room to sit on one line.
const WIDTH = 372;
/// Gap between the sparkle button and the menu.
const GAP = 4;
/// Keeps the menu off the very edge of the viewport when it has to be nudged back inside.
const MARGIN = 8;

/// A context window as binary K or M: 131,072 reads "128K", not "131K".
///
/// Rounded on 1024 deliberately. Model documentation quotes these windows in binary units, so dividing by
/// 1000 would print a number that matches nothing the user has read anywhere else. The exact count is never
/// lost - it is on the chip's tooltip.
export function formatContext(tokens: number): string {
  if (tokens >= 1024 * 1024) return `${+(tokens / 1024 / 1024).toFixed(1)}M`;
  return `${Math.round(tokens / 1024)}K`;
}

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
            className="fixed z-30 overflow-hidden rounded-[10px] border border-slate-400 bg-white shadow-xl dark:border-gray-700 dark:bg-gray-800"
          >
            {/* A darker border than the app's usual gray-200, and the same colour on both dividers, so the
                menu reads as its own surface where it overhangs the workspace behind the panel. */}
            <div className="flex items-center gap-2 border-b border-slate-400 px-3 pb-2 pt-2.5 dark:border-white/[0.07]">
              <span className="text-indigo-500 dark:text-indigo-400">
                <SparkleIcon size={15} />
              </span>
              <span className="text-[11px] font-bold uppercase tracking-[0.09em] text-gray-700 dark:text-gray-200">
                {t("modelPickerTitle")}
              </span>
            </div>

            {/* Only the rows scroll. A scroll container over the whole menu would carry the title and the
                legend away exactly when a long list makes them worth having. */}
            <div data-testid="model-rows" className="max-h-64 overflow-y-auto py-1">
              {models.map((m) => {
                const isSelected = m.id === (selected?.id ?? null);
                return (
                  <button
                    key={m.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={isSelected}
                    onClick={() => {
                      onSelect(m.id);
                      setOpen(false);
                    }}
                    title={m.label}
                    className={`flex w-full items-center gap-2 px-3 py-[7px] text-left ${
                      isSelected
                        ? "bg-indigo-600/[0.07] dark:bg-indigo-400/[0.12]"
                        : "hover:bg-gray-100 dark:hover:bg-white/5"
                    }`}
                  >
                    {/* max-w is the one departure from the design, which asks for no ellipsis at all. An
                        imported name is whatever the endpoint calls it, and an unbounded one overflowed the
                        menu and pushed the context length out of sight - fixed in 0.232.1 (PR #558) and not
                        worth reintroducing. At realistic lengths it never truncates. */}
                    <span
                      className={`max-w-[55%] shrink-0 truncate pr-[3px] text-[13.5px] ${
                        isSelected
                          ? "font-semibold text-indigo-600 dark:text-indigo-300"
                          : "text-gray-700 dark:text-gray-200"
                      }`}
                    >
                      {m.label}
                    </span>

                    {/* The element that gives way when a row is tight. min-w-0 is load-bearing: a flex item
                        will not shrink below its content without it. A model with no description gets the
                        empty spacer, so the icons and the chip stay right-aligned either way. */}
                    {m.description ? (
                      <span className="min-w-0 flex-1 truncate text-[11px] text-gray-500 dark:text-gray-400">
                        {m.description}
                      </span>
                    ) : (
                      <span className="min-w-0 flex-1" />
                    )}

                    <span
                      className={`inline-flex shrink-0 items-center gap-[5px] ${
                        isSelected ? "text-gray-600 dark:text-gray-300" : "text-gray-500 dark:text-gray-500"
                      }`}
                    >
                      {m.supportsTools && (
                        <Capability label={t("modelCallsTools")}>
                          <BriefcaseIcon />
                        </Capability>
                      )}
                      {/* Without this the composer's "Select a vision model" warning names a remedy the
                          user has no way to act on - nothing else in the product says which models see. */}
                      {m.supportsImages && (
                        <Capability label={t("modelReadsImages")}>
                          <EyeIcon />
                        </Capability>
                      )}
                    </span>

                    <span
                      title={t("modelContextTokens", { tokens: m.contextLength.toLocaleString() })}
                      className={`shrink-0 rounded-[5px] px-1.5 py-0.5 text-[10.5px] font-bold tabular-nums ${
                        isSelected
                          ? "bg-indigo-600/[0.12] text-indigo-600 dark:bg-indigo-400/20 dark:text-indigo-300"
                          : "bg-gray-100 text-gray-500 dark:bg-white/[0.06] dark:text-gray-400"
                      }`}
                    >
                      {formatContext(m.contextLength)}
                    </span>
                  </button>
                );
              })}
            </div>

            {/* The icons are the only wordless thing on a row. */}
            <div className="flex items-center gap-2.5 whitespace-nowrap border-t border-slate-400 px-3 py-[7px] text-[10.5px] text-gray-500 dark:border-white/[0.07] dark:text-gray-500">
              <span className="inline-flex items-center gap-1">
                <BriefcaseIcon size={12} />
                {t("modelCallsTools")}
              </span>
              <span className="inline-flex items-center gap-1">
                <EyeIcon size={12} />
                {t("modelReadsImages")}
              </span>
            </div>
          </div>,
          document.body,
        )}
    </div>
  );
}

/// A four-point sparkle. Inline like ChatPanel's other toolbar icons rather than a dependency, and filled
/// rather than stroked so it still reads at 16px.
const SparkleIcon = ({ size = 16 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M12 2l2.2 6.1L20.4 10l-6.2 1.9L12 18l-2.2-6.1L3.6 10l6.2-1.9L12 2z" />
    <path d="M18.5 15l.9 2.5 2.6.8-2.6.8-.9 2.5-.9-2.5-2.6-.8 2.6-.8.9-2.5z" opacity="0.7" />
  </svg>
);

/// The same glyphs the app already uses, inlined rather than imported. `EyeIcon` in `detail/icons.tsx` is a
/// fixed-size ReactElement bound to `iconProps`, and the briefcase lives inside `MeetingTypeIcon`'s private
/// PATHS record - neither is sizable from here without refactoring a file this change has no business
/// touching. Same path data, local wrapper.
const BriefcaseIcon = ({ size = 14 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor"
       strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M2 6h12v7H2V6zM6 6V4h4v2M2 9h12" />
  </svg>
);

const EyeIcon = ({ size = 14 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
       strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z" /><circle cx="12" cy="12" r="3" />
  </svg>
);

/// One capability marker. `role="img"` with a label rather than `aria-hidden`, so the capability joins the
/// row's accessible name - a screen reader user gets the same information the legend gives a sighted one.
const Capability = ({ label, children }: { label: string; children: React.ReactNode }) => (
  <span role="img" aria-label={label} title={label} className="inline-flex">
    {children}
  </span>
);
