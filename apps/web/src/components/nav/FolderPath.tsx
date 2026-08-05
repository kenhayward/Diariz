import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { collapsePath, type PathCrumb } from "../../lib/folderPath";
import { ChevronDownIcon, ChevronRightIcon } from "../icons";

/// A folder path, collapsed to fit, with a menu listing the full ancestor chain.
///
/// Presentational on purpose: it takes crumbs and callbacks and knows nothing about drilling, routing or
/// rooms. Three places need a folder path - the nav's drill breadcrumb, a folder's own page, and search
/// results - and they disagree about what clicking one means, so that decision stays with the caller.
///
/// The **trailing chevron** is the menu trigger and is always present, so the full hierarchy is one click
/// away whether or not the path is collapsed. The collapsed `…` is a plain indicator, not a second trigger:
/// two controls opening the same menu is not affordable in a strip this narrow.
export default function FolderPath({
  crumbs,
  maxVisible = 3,
  onSelect,
  extraItems = [],
  onCrumbDrop,
}: {
  /// Root first, current folder last.
  crumbs: PathCrumb[];
  maxVisible?: number;
  /// Clicking a crumb or a menu entry. Omit to render the path as static text.
  onSelect?: (id: string) => void;
  /// Menu entries shown above the ancestor chain (the nav puts "Open section page" here).
  extraItems?: { label: string; onClick: () => void }[];
  /// A recording dropped onto a crumb - the cheap way to move something up a level.
  onCrumbDrop?: (crumbId: string, recordingId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Close on an outside click or Escape, like the other menus in the panel.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (crumbs.length === 0) return null;

  const segments = collapsePath(crumbs, maxVisible);

  return (
    <div ref={wrapRef} className="relative flex min-w-0 flex-1 items-center gap-0.5">
      <nav className="flex min-w-0 flex-1 items-center gap-0.5 overflow-hidden">
        {segments.map((seg, i) => (
          <span key={seg === "ellipsis" ? `gap-${i}` : seg.id} className="flex min-w-0 items-center gap-0.5">
            {i > 0 && (
              <span className="shrink-0 text-gray-300 dark:text-gray-600" aria-hidden>
                <ChevronRightIcon size={11} />
              </span>
            )}
            {seg === "ellipsis" ? (
              <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500" title={t("folderPathCollapsed")}>
                &hellip;
              </span>
            ) : onSelect ? (
              <button
                type="button"
                onClick={() => onSelect(seg.id)}
                onDragOver={onCrumbDrop ? (e) => e.preventDefault() : undefined}
                onDrop={
                  onCrumbDrop
                    ? (e) => {
                        const dragged = e.dataTransfer.getData("text/plain");
                        if (!dragged) return;
                        e.stopPropagation();
                        onCrumbDrop(seg.id, dragged);
                      }
                    : undefined
                }
                className="min-w-0 truncate text-[11.5px] text-gray-600 hover:underline dark:text-gray-300"
              >
                {seg.name}
              </button>
            ) : (
              <span className="min-w-0 truncate text-[11.5px] text-gray-600 dark:text-gray-300">{seg.name}</span>
            )}
          </span>
        ))}
      </nav>

      <button
        type="button"
        aria-label={t("folderPathMenu")}
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="shrink-0 rounded p-0.5 text-gray-400 hover:bg-gray-100 dark:text-gray-500 dark:hover:bg-gray-800"
      >
        <ChevronDownIcon size={12} />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-20 mt-1 min-w-[12rem] rounded border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {extraItems.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                item.onClick();
              }}
              className="block w-full px-3 py-1 text-left text-xs text-blue-600 hover:bg-gray-50 dark:text-blue-400 dark:hover:bg-gray-800"
            >
              {item.label}
            </button>
          ))}
          {extraItems.length > 0 && <div className="my-1 border-t dark:border-gray-700" />}
          {/* The FULL chain, not the collapsed one - the menu is how a hidden ancestor stays reachable.
              Indented by depth so the shape of the path is legible without repeating parent names. */}
          {crumbs.map((c, depth) => (
            <button
              key={c.id}
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onSelect?.(c.id);
              }}
              style={{ paddingLeft: `${0.75 + depth * 0.6}rem` }}
              className="block w-full truncate py-1 pr-3 text-left text-xs text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {c.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
