import { useEffect, useRef, useState } from "react";

export interface KebabAction {
  label: string;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
}

/// A "⋮" button that opens a popover of actions. Closes on outside-click or Escape.
/// Router/query-free so it can be reused on any list row or header.
export default function KebabMenu({
  actions,
  label = "Actions",
}: {
  actions: KebabAction[];
  label?: string;
}) {
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

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setOpen((v) => !v);
        }}
        className="rounded px-2 py-1 text-lg leading-none text-gray-500 hover:bg-gray-100"
      >
        ⋮
      </button>
      {open && (
        <div
          role="menu"
          className="absolute right-0 z-10 mt-1 w-44 overflow-hidden rounded-lg border bg-white py-1 shadow-lg"
        >
          {actions.map((a) => (
            <button
              key={a.label}
              type="button"
              role="menuitem"
              disabled={a.disabled}
              onClick={(e) => {
                e.preventDefault();
                e.stopPropagation();
                setOpen(false);
                a.onClick();
              }}
              className={`block w-full px-3 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-40 ${
                a.danger ? "text-red-600" : "text-gray-700"
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
