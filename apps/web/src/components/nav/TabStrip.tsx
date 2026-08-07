import { useTranslation } from "react-i18next";
import type { PanelTab } from "../../lib/panelTab";

/// Vertical List / Calendar / Actions tabs, sitting to the left of the panel's scroll area (below the toolbar).
function TabStrip({
  tab,
  onSelect,
}: {
  tab: PanelTab;
  onSelect: (t: PanelTab) => void;
}) {
  const { t } = useTranslation("workspace");
  const item = (key: PanelTab, label: string) => (
    <button
      type="button"
      onClick={() => onSelect(key)}
      aria-pressed={tab === key}
      className={`w-full px-2 py-2 text-[11px] font-medium [writing-mode:vertical-rl] ${
        tab === key
          ? "bg-white text-blue-700 dark:bg-gray-900 dark:text-blue-300"
          : "text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
      }`}
    >
      {label}
    </button>
  );
  return (
    <div className="flex w-7 shrink-0 flex-col items-stretch border-r bg-gray-50 dark:border-gray-800 dark:bg-gray-950/40">
      {item("list", t("tabList"))}
      {item("calendar", t("tabCalendar"))}
      {item("actions", t("tabActions"))}
      {item("tags", t("tabTags"))}
    </div>
  );
}
export default TabStrip;
