import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { BackIcon, ChevronRightIcon } from "./SectionIcons";

/// The header of a drilled-in section: a back chip to the hub, the section's name, and an optional
/// right-hand slot (the Transcript uses it for its "142 segments · 21 min" count). This replaces the tab
/// strip as the way back out, so the back chip is the only exit and must always be present.

export default function SectionBreadcrumb({
  title,
  icon,
  onBack,
  right,
}: {
  title: string;
  icon?: ReactNode;
  onBack: () => void;
  right?: ReactNode;
}) {
  const { t } = useTranslation("workspace");

  return (
    <nav aria-label={title} className="flex items-center gap-2.5 text-sm text-gray-500 dark:text-gray-400">
      <button
        type="button"
        onClick={onBack}
        className="inline-flex items-center gap-1.5 rounded-lg border border-gray-200 bg-white px-2.5 py-1 text-gray-900 hover:bg-gray-50 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100 dark:hover:bg-gray-700"
      >
        <BackIcon />
        {t("hubBackToOverview")}
      </button>
      <span className="text-gray-400 dark:text-gray-500" aria-hidden>
        <ChevronRightIcon size={14} />
      </span>
      <span
        aria-current="page"
        className="inline-flex items-center gap-1.5 font-semibold text-blue-600 dark:text-blue-400"
      >
        {icon}
        {title}
      </span>
      {right && <span className="ml-auto text-xs text-gray-500 dark:text-gray-400">{right}</span>}
    </nav>
  );
}
