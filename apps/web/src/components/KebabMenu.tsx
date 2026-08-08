import { useEffect, useRef, useState } from "react";

/// Enough of the popover to decide which way it should open. It varies with the number of actions, but a
/// single number is what keeps this a measurement of the trigger rather than of a node not yet rendered.
const MENU_HEIGHT = 200;

export interface KebabAction {
  label: string;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
  /// Why this item is unavailable. Shown as the item's tooltip - a disabled action that does not say why
  /// reads as a broken menu rather than a rule, and the label alone rarely carries the reason.
  title?: string;
}

/// A "⋮" button that opens a popover of actions. Closes on outside-click or Escape.
/// Router/query-free so it can be reused on any list row or header.
export default function KebabMenu({
  actions,
  label = "Actions",
  buttonClassName = "rounded px-2 py-1 text-lg leading-none text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800",
}: {
  actions: KebabAction[];
  label?: string;
  /// Classes for the trigger. The default is sized for a list row (~28px tall); the calendar day grid
  /// passes a compact variant because a 30-minute meeting's block is only 22px.
  buttonClassName?: string;
}) {
  const [open, setOpen] = useState(false);
  // Which way the popover goes, decided at the moment of opening. A menu on the last row of a scrolling
  // panel would otherwise open into its bottom edge and be cut off.
  const [up, setUp] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);

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
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        ref={trigger}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          // Roughly the popover's own height: enough to tell "this will not fit" without measuring a
          // node that does not exist yet.
          const room = window.innerHeight - (trigger.current?.getBoundingClientRect().bottom ?? 0);
          setUp(room < MENU_HEIGHT);
          setOpen((v) => !v);
        }}
        className={buttonClassName}
      >
        ⋮
      </button>
      {open && (
        <div
          role="menu"
          // z-30 keeps the menu above sticky headers (the recording detail tab strip is z-10), which would
          // otherwise paint over the menu's top items.
          className={`absolute right-0 z-30 w-44 overflow-hidden rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900 ${
            up ? "bottom-full mb-1" : "mt-1"
          }`}
        >
          {actions.map((a) => (
            <button
              key={a.label}
              type="button"
              role="menuitem"
              disabled={a.disabled}
              title={a.title}
              onClick={(e) => {
                e.preventDefault();
                e.stopPropagation();
                setOpen(false);
                a.onClick();
              }}
              className={`block w-full px-3 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-40 dark:hover:bg-gray-800 ${
                a.danger ? "text-red-600 dark:text-red-400" : "text-gray-700 dark:text-gray-200"
              }`}
            >
              {a.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
