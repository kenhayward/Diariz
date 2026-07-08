import { useTranslation } from "react-i18next";
import { fontSizeFor, tagColor } from "../lib/tagCloud";
import type { TagCloudEntry } from "../lib/types";

/// The flat weighted tag cloud: a centred flex-wrap of clickable tags. Font size AND a subtle colour ramp
/// (blue -> violet) both scale with aggregate weight, so more important tags stand out two ways; no
/// rotation (the modern readable form). Alphabetical so the layout stays stable as weights drift. Tight,
/// slightly-overlapping spacing keeps a large cloud compact. Clicking a tag selects it; clicking the
/// selected tag deselects (the parent owns the state, so the left panel and the expanded modal share one
/// selection). Renders nothing when there are no tags. Compact px bounds by default; the modal passes
/// larger ones.
export default function TagCloud({
  tags,
  selected,
  onSelect,
  minPx = 11,
  maxPx = 22,
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
    <div className="flex flex-wrap items-baseline justify-center gap-x-2 gap-y-0 px-3 py-3 leading-none">
      {sorted.map((entry) => {
        const isSelected = selected === entry.tag;
        return (
          <button
            key={entry.tag}
            type="button"
            aria-pressed={isSelected}
            title={t("tagTooltip", { count: entry.count })}
            onClick={() => onSelect(isSelected ? null : entry.tag)}
            // Inline colour by weight (the calendar-colour precedent). Selected tags drop the ramp for the
            // theme's blue + bold + underline so the choice is unmistakable; unselected use the ramp with a
            // neutral hover background (a hover text colour would fight the inline colour).
            style={{
              fontSize: `${fontSizeFor(entry.weight, minW, maxW, minPx, maxPx)}px`,
              color: isSelected ? undefined : tagColor(entry.weight, minW, maxW),
            }}
            className={`rounded px-1 py-0.5 leading-none transition-colors ${
              isSelected
                ? "font-bold text-blue-700 underline underline-offset-4 dark:text-blue-300"
                : "hover:bg-gray-100 dark:hover:bg-gray-800"
            }`}
          >
            {entry.tag}
          </button>
        );
      })}
    </div>
  );
}
