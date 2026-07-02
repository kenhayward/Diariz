import type { ReactNode } from "react";

/// One tab on the recording detail page: a label, an optional toolbar (rendered in a bar directly below the
/// tab strip), and the content shown when the tab is active.
export interface DetailTab {
  key: string;
  label: string;
  toolbar?: ReactNode;
  content: ReactNode;
}

/// A horizontal tab strip for the recording detail page (Overview / Minutes / Actions / Speakers / Transcript /
/// Attachments). The strip and the active tab's toolbar are pinned to the top of the scroll container while the
/// content scrolls beneath them. Controlled: the parent owns the active key (and persists it).
export default function DetailTabs({
  tabs,
  active,
  onSelect,
}: {
  tabs: DetailTab[];
  active: string;
  onSelect: (key: string) => void;
}) {
  const current = tabs.find((tb) => tb.key === active) ?? tabs[0];
  if (!current) return null;

  return (
    <div>
      {/* Strip + toolbar pin to the top of the scroll area so the active tab's controls (incl. the transcript
          mini-player) stay visible while its content scrolls with the page. */}
      <div className="sticky top-0 z-10 bg-white dark:bg-gray-900">
        <div role="tablist" className="flex flex-wrap gap-1 border-b dark:border-gray-700">
          {tabs.map((tb) => {
            const isActive = tb.key === current.key;
            return (
              <button
                key={tb.key}
                type="button"
                role="tab"
                aria-selected={isActive}
                onClick={() => onSelect(tb.key)}
                className={`-mb-px rounded-t border-b-2 px-3 py-1.5 text-sm ${
                  isActive
                    ? "border-gray-900 font-medium text-gray-900 dark:border-gray-100 dark:text-gray-100"
                    : "border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
                }`}
              >
                {tb.label}
              </button>
            );
          })}
        </div>
        {current.toolbar && (
          <div className="flex flex-wrap items-center gap-0.5 border-b bg-gray-50 px-2 py-1.5 dark:border-gray-700 dark:bg-gray-800/40">
            {current.toolbar}
          </div>
        )}
      </div>
      <div role="tabpanel" className="pt-3">
        {current.content}
      </div>
    </div>
  );
}
