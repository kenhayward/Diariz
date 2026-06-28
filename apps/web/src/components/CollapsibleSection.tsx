import { useState, type ReactNode } from "react";

/// A bordered content panel with a collapsible header. The whole header strip (title → chevron) is the
/// click target, with a subtle background that darkens on hover, and the chevron matches the kebab scale.
/// Used for the Summary / Speakers / Actions / Transcript panels on the recording detail page.
export default function CollapsibleSection({
  title,
  defaultCollapsed = false,
  bodyClassName = "px-4 pb-4",
  children,
}: {
  title: string;
  defaultCollapsed?: boolean;
  /// Classes for the body wrapper. Defaults to padded; pass a flush value (no horizontal padding) when
  /// the content should keep the panel's full width (e.g. the transcript segment rows).
  bodyClassName?: string;
  children: ReactNode;
}) {
  const [collapsed, setCollapsed] = useState(defaultCollapsed);

  return (
    <div className="overflow-hidden rounded-lg border bg-white dark:border-gray-700 dark:bg-gray-900">
      <button
        type="button"
        aria-label={`${collapsed ? "Expand" : "Collapse"} ${title} section`}
        aria-expanded={!collapsed}
        onClick={() => setCollapsed((v) => !v)}
        className="flex w-full items-center justify-between bg-gray-50 px-4 py-2 text-left hover:bg-gray-100 dark:bg-gray-800/40 dark:hover:bg-gray-800/70"
      >
        <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400">{title}</h2>
        <span aria-hidden className="text-lg leading-none text-gray-400 dark:text-gray-500">
          {collapsed ? "▸" : "▾"}
        </span>
      </button>
      {!collapsed && <div className={`pt-3 ${bodyClassName}`}>{children}</div>}
    </div>
  );
}
