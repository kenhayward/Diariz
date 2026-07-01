import { useState, type ReactNode } from "react";

/// A bordered content panel with a collapsible header. Clicking the title toggles collapse; an optional
/// `headerActions` toolbar sits between the title and the collapse chevron, which is the **last** control on
/// the strip (its own button, so the toolbar buttons never toggle the section). Subtle background that
/// darkens on hover; the chevron matches the kebab scale.
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
        className={`flex shrink-0 items-center rounded-t-lg bg-gray-50 pr-1 hover:bg-gray-100 dark:bg-gray-800/40 dark:hover:bg-gray-800/70 ${
          collapsed ? "rounded-b-lg" : ""
        }`}
      >
        {/* Title is a heading (not a button) so it never collides with same-named controls elsewhere
            (e.g. the "Actions" kebab), but clicking it — and the empty header space up to the toolbar —
            toggles the section. The chevron (the LAST item on the strip, after the toolbar) is the
            keyboard-accessible toggle and stays in the same place across panels. */}
        <h2
          className="flex-1 cursor-pointer px-4 py-2 text-sm font-medium text-gray-500 dark:text-gray-400"
          onClick={() => setCollapsed((v) => !v)}
        >
          {title}
        </h2>
        {headerActions && <div className="flex items-center gap-0.5">{headerActions}</div>}
        <button
          type="button"
          aria-label={`${collapsed ? "Expand" : "Collapse"} ${title} section`}
          aria-expanded={!collapsed}
          onClick={() => setCollapsed((v) => !v)}
          className="ml-0.5 flex items-center px-2 py-2 text-lg leading-none text-gray-400 hover:text-gray-600 dark:text-gray-500 dark:hover:text-gray-300"
        >
          <span aria-hidden>{collapsed ? "▸" : "▾"}</span>
        </button>
      </div>
      {!collapsed && (
        <div className={`pt-3 ${stickyFill ? "min-h-0 flex-1 overflow-y-auto" : ""} ${bodyClassName}`}>{children}</div>
      )}
    </div>
  );
}
