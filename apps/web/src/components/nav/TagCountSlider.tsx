import { useTranslation } from "react-i18next";

/// A slider that limits how many tags the cloud shows (the most-used first). Its own file because the panel's
/// Tags tab and the expanded tag-cloud modal both use it, and it must be the exact same control in each.
/// Hidden when there are 2 or fewer tags (nothing to trim).
export function TagCountSlider({
  value,
  max,
  onChange,
}: {
  value: number;
  max: number;
  onChange: (n: number) => void;
}) {
  const { t } = useTranslation("workspace");
  if (max <= 2) return null;
  return (
    <div className="flex min-w-0 flex-1 items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
      <label htmlFor="tag-count-slider" className="shrink-0">
        {t("tagCountLabel", { count: value })}
      </label>
      <input
        id="tag-count-slider"
        type="range"
        min={1}
        max={max}
        value={value}
        aria-label={t("tagCountLabel", { count: value })}
        onChange={(e) => onChange(Number(e.target.value))}
        className="min-w-0 flex-1 accent-blue-600"
      />
    </div>
  );
}
