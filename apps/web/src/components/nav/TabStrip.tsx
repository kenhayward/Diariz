import { useTranslation } from "react-i18next";
import type { PanelTab } from "../../lib/panelTab";

/// Horizontal List / Calendar / Actions / Tags tabs, between the list toolbar and the active tab's body.
/// Styling is deliberately identical to DetailTabs' strip so the app has one horizontal-tab language;
/// keep the two in step if either changes.
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
        type="button"
        role="tab"
        aria-selected={isActive}
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
    <div role="tablist" className="flex shrink-0 gap-1 border-b px-1.5 dark:border-gray-700">
      {item("list", t("tabList"))}
      {item("calendar", t("tabCalendar"))}
      {item("actions", t("tabActions"))}
      {item("tags", t("tabTags"))}
    </div>
  );
}
export default TabStrip;
