import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { collapsePath, type PathCrumb } from "../../lib/folderPath";
import { ChevronDownIcon, ChevronRightIcon } from "../icons";

/// A folder path, collapsed to fit, with a menu listing the full ancestor chain.
///
/// Presentational on purpose: it takes crumbs, callbacks and (for a menu entry that should behave as a
/// real link) a router destination, and knows nothing about drilling or rooms. Two places render this
/// component - the nav's drill breadcrumb and a folder's own page - and they disagree about what clicking
/// a crumb means, so that decision stays with the caller. Search results collapse a path the same way but
/// do not use this component: a result row is already the click target, so nesting FolderPath's own
/// trailing button inside it would be invalid HTML (see SearchBar.tsx).
///
/// The **trailing chevron** is the menu trigger and is always present, so the full hierarchy is one click
/// away whether or not the path is collapsed. The collapsed `…` is a plain indicator, not a second trigger:
/// two controls opening the same menu is not affordable in a strip this narrow. Anything a caller wants
/// beside the trigger goes in `trailingAction`, which sits between the path and the chevron.
export default function FolderPath({
  crumbs,
  maxVisible = 3,
  onSelect,
  trailingAction,
  onCrumbDrop,
  label,
  menuLabel,
}: {
  /// Root first, current folder last.
  crumbs: PathCrumb[];
  maxVisible?: number;
  /// Clicking a crumb or a menu entry. Omit to render the path as static text.
  onSelect?: (id: string) => void;
  /// A control rendered between the path and the menu trigger - the nav puts the folder-page button
  /// here. Kept as an opaque node so this component stays presentational: it places the control, and
  /// knows nothing about where it goes.
  trailingAction?: ReactNode;
  /// A recording dropped onto a crumb - the cheap way to move something up a level.
  onCrumbDrop?: (crumbId: string, recordingId: string) => void;
  /// Overrides the nav landmark's accessible name. Two instances of this component can be on screen at
  /// once (the nav's drill breadcrumb and a folder page's own path) - without this they'd share the same
  /// "Folder path" name and a screen-reader user browsing by landmark could not tell them apart. Defaults
  /// to the generic `folderPathLabel` string.
  label?: string;
  /// Same idea, for the menu trigger's accessible name (defaults to `folderPathMenu`).
  menuLabel?: string;
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
      <nav
        aria-label={label ?? t("folderPathLabel")}
        className="flex min-w-0 flex-1 items-center gap-0.5 overflow-hidden"
      >
        {segments.map((seg, i) => {
          // The last crumb is the folder you are already in: convention is that it's static, not another
          // control. As a button it was a functional no-op (clicking it re-opened the page/re-drilled to
          // where you already were) that still pushed a history entry, so browser Back appeared to do
          // nothing.
          const isCurrent = i === segments.length - 1;
          const dropHandlers =
            seg !== "ellipsis" && onCrumbDrop
              ? {
                  onDragOver: (e: React.DragEvent) => e.preventDefault(),
                  onDrop: (e: React.DragEvent) => {
                    const dragged = e.dataTransfer.getData("text/plain");
                    if (!dragged) return;
                    e.stopPropagation();
                    onCrumbDrop(seg.id, dragged);
                  },
                }
              : {};

          return (
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
              ) : onSelect && !isCurrent ? (
                <button
                  type="button"
                  onClick={() => onSelect(seg.id)}
                  {...dropHandlers}
                  className="min-w-0 truncate text-[11.5px] text-gray-600 hover:underline dark:text-gray-300"
                >
                  {seg.name}
                </button>
              ) : (
                <span
                  aria-current={isCurrent ? "page" : undefined}
                  {...dropHandlers}
                  className="min-w-0 truncate text-[11.5px] text-gray-600 dark:text-gray-300"
                >
                  {seg.name}
                </span>
              )}
            </span>
          );
        })}
      </nav>

      {trailingAction}

      <button
        type="button"
        aria-label={menuLabel ?? t("folderPathMenu")}
        aria-haspopup="menu"
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
