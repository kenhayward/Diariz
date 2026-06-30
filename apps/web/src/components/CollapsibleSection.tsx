import { useState, type ReactNode } from "react";

/// A bordered content panel with a collapsible header. The title→chevron strip toggles collapse; an
/// optional `headerActions` toolbar sits to the left of the chevron (its own buttons, so it never toggles
/// the section). Subtle background that darkens on hover; the chevron matches the kebab scale.
/// Used for the Summary / Speakers / Actions / Transcript panels on the recording detail page.
export default function CollapsibleSection({
  title,
  defaultCollapsed = false,
  bodyClassName = "px-4 pb-4",
  headerActions,
  stickyFill = false,
  children,
}: {
  title: string;
  defaultCollapsed?: boolean;
  /// Classes for the body wrapper. Defaults to padded; pass a flush value (no horizontal padding) when
  /// the content should keep the panel's full width (e.g. the transcript segment rows).
  bodyClassName?: string;
  /// Optional action buttons rendered in the header (e.g. a small toolbar). Rendered as a sibling of the
  /// toggle button so clicking them doesn't expand/collapse the section.
  headerActions?: ReactNode;
  /// Opt-in: pin this panel to the top of the scroll container once reached and let its body fill the rest
  /// of the viewport and scroll internally (used by the transcript). The body must own its own scroll, so
  /// pass a flush `bodyClassName`. Off by default — other panels flow normally.
  stickyFill?: boolean;
  children: ReactNode;
}) {
  const [collapsed, setCollapsed] = useState(defaultCollapsed);

  return (
    // No `overflow-hidden` here: it would clip popovers that escape the panel (e.g. the SpeakerAssign
    // typeahead on the bottom row) and, by making the panel a scroll container, let a focused near-bottom
    // input scroll the header out of view. The header's grey strip is rounded directly instead.
    <div
      className={`rounded-lg border bg-white dark:border-gray-700 dark:bg-gray-900 ${
        // Sticky-fill: pin near the top and cap the panel to the viewport so the body (an internal scroll
        // area) takes over once the panel reaches the top; hand back to the page at the body's edges.
        stickyFill && !collapsed ? "sticky top-0 z-10 flex max-h-[calc(100vh-5rem)] flex-col" : ""
      }`}
    >
      <div
        className={`flex shrink-0 items-center rounded-t-lg bg-gray-50 pr-2 hover:bg-gray-100 dark:bg-gray-800/40 dark:hover:bg-gray-800/70 ${
          collapsed ? "rounded-b-lg" : ""
        }`}
      >
        <button
          type="button"
          aria-label={`${collapsed ? "Expand" : "Collapse"} ${title} section`}
          aria-expanded={!collapsed}
          onClick={() => setCollapsed((v) => !v)}
          className="flex flex-1 items-center justify-between px-4 py-2 text-left"
        >
          <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400">{title}</h2>
          <span aria-hidden className="text-lg leading-none text-gray-400 dark:text-gray-500">
            {collapsed ? "▸" : "▾"}
          </span>
        </button>
        {headerActions && <div className="flex items-center gap-0.5 pl-1">{headerActions}</div>}
      </div>
      {!collapsed && (
        <div className={`pt-3 ${stickyFill ? "min-h-0 flex-1 overflow-y-auto" : ""} ${bodyClassName}`}>{children}</div>
      )}
    </div>
  );
}
