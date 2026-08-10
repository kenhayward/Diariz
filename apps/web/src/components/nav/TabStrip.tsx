import { useTranslation } from "react-i18next";
import type { PanelTab } from "../../lib/panelTab";

/// The single tabpanel id every tab points to via `aria-controls`. RecordingsPanel swaps the body in place
/// for whichever tab is active rather than rendering four panels at once, so one shared id is enough - which
/// tab is "for" the panel at any moment is carried by the panel's own `aria-labelledby` (see `tabId` below),
/// not by a different `aria-controls` target per tab.
export const TABPANEL_ID = "recordings-tabpanel";

/// A tab's stable DOM id, so the tab body (a plain `role="tabpanel"` div in RecordingsPanel) can point
/// `aria-labelledby` back at whichever tab is currently active.
export function tabId(tab: PanelTab): string {
  return `recordings-tab-${tab}`;
}

/// Horizontal List / Calendar / Actions / Tags tabs, between the list toolbar and the active tab's body.
/// The active/inactive ink is byte-identical to DetailTabs' strip so the two read as one tab language;
/// the metrics are deliberately tighter (12.5px labels, no wrap) because four tabs have to fit across a
/// 288px panel, where DetailTabs' text-sm + flex-wrap would wrap. Keep the ink treatment in step with
/// DetailTabs; the metrics are this strip's own.
function TabStrip({
  tab,
  onSelect,
}: {
  tab: PanelTab;
  onSelect: (t: PanelTab) => void;
}) {
  const { t } = useTranslation("workspace");
  const item = (key: PanelTab, label: string) => {
    const isActive = tab === key;
    return (
      <button
        id={tabId(key)}
        type="button"
        role="tab"
        aria-selected={isActive}
        aria-controls={TABPANEL_ID}
        onClick={() => onSelect(key)}
        className={`-mb-px border-b-2 px-3 py-2 text-[12.5px] ${
          isActive
            ? "border-gray-900 font-medium text-gray-900 dark:border-gray-100 dark:text-gray-100"
            : "border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
        }`}
      >
        {label}
      </button>
    );
  };
  return (
    <div role="tablist" aria-label={t("panelTabsLabel")} className="flex shrink-0 gap-1 border-b px-1.5 dark:border-gray-700">
      {item("list", t("tabList"))}
      {item("calendar", t("tabCalendar"))}
      {item("actions", t("tabActions"))}
      {item("tags", t("tabTags"))}
    </div>
  );
}
export default TabStrip;
