import { useTranslation } from "react-i18next";
import { fontSizeFor } from "../lib/tagCloud";
import type { TagCloudEntry } from "../lib/types";

/// The flat weighted tag cloud: a centred flex-wrap of clickable tags, font size log-scaled by aggregate
/// weight (no rotation - the modern readable form), alphabetical so the layout stays stable as weights
/// drift. Clicking a tag selects it; clicking the selected tag deselects (the parent owns the state, so
/// the left panel and the expanded modal share one selection). Renders nothing when there are no tags.
export default function TagCloud({
  tags,
  selected,
  onSelect,
  minPx = 12,
  maxPx = 28,
}: {
  tags: TagCloudEntry[];
  selected: string | null;
  onSelect: (tag: string | null) => void;
  minPx?: number;
  maxPx?: number;
}) {
  const { t } = useTranslation("workspace");
  if (tags.length === 0) return null;

  const weights = tags.map((x) => x.weight);
  const minW = Math.min(...weights);
  const maxW = Math.max(...weights);
  const sorted = [...tags].sort((a, b) => a.tag.localeCompare(b.tag));

  return (
    <div className="flex flex-wrap items-baseline justify-center gap-x-3 gap-y-1 px-3 py-4">
      {sorted.map((entry) => {
        const isSelected = selected === entry.tag;
        return (
          <button
            key={entry.tag}
            type="button"
            aria-pressed={isSelected}
            title={t("tagTooltip", { count: entry.count })}
            onClick={() => onSelect(isSelected ? null : entry.tag)}
            style={{ fontSize: `${fontSizeFor(entry.weight, minW, maxW, minPx, maxPx)}px` }}
            className={`leading-tight transition-colors ${
              isSelected
                ? "font-semibold text-blue-700 dark:text-blue-300"
                : "text-gray-600 hover:text-blue-600 dark:text-gray-300 dark:hover:text-blue-400"
            }`}
          >
            {entry.tag}
          </button>
        );
      })}
    </div>
  );
}
