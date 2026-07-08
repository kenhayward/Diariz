import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import TagCloud from "./TagCloud";
import { RecordingRow, TagCountSlider } from "./RecordingsPanel";
import { recordingsForTags, topTagsByCount } from "../lib/tagCloud";
import type { RecordingSummary, TagCloudEntry } from "../lib/types";

/// The expanded tag-cloud view: a large modal (~80% of the page) with a bigger cloud on top and the
/// matching recordings below. The tag selection is the PARENT's state (shared with the left panel's
/// cloud), so picking a tag here filters the panel behind too and survives closing; the count slider is
/// local (the modal can show more than the compact panel). Recordings render with the same row as the
/// list/calendar (icons, duration, date/time); clicking one closes the modal and opens it. ✕/Escape close
/// without navigating (no outside-click close - repo convention). Mirrors the ManageMeetingTypesModal shell.
export default function TagCloudModal({
  tags,
  recordings,
  selected,
  onSelect,
  onClose,
}: {
  tags: TagCloudEntry[];
  recordings: RecordingSummary[];
  selected: string | null;
  onSelect: (tag: string | null) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [limit, setLimit] = useState(() => Math.min(tags.length, 80));

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const shownTags = useMemo(() => topTagsByCount(tags, limit), [tags, limit]);
  const items = useMemo(() => recordingsForTags(recordings, shownTags, selected), [recordings, shownTags, selected]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("workspace:tagCloudTitle")}
        className="flex h-[85vh] w-[80vw] max-w-6xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between border-b px-5 py-3 dark:border-gray-700">
          <h2 className="text-base font-semibold dark:text-gray-100">{t("workspace:tagCloudTitle")}</h2>
          <button
            type="button"
            aria-label={t("common:close")}
            onClick={onClose}
            className="rounded p-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            ✕
          </button>
        </div>

        {/* The tag panel is user-resizable (drag the bottom edge) so you can grow it to show more tags at once. */}
        <div
          data-testid="tag-cloud-panel"
          className="shrink-0 resize-y overflow-auto border-b dark:border-gray-700"
          style={{ height: "40%", minHeight: "6rem", maxHeight: "80%" }}
        >
          <div className="flex px-4 pt-3">
            <TagCountSlider value={Math.min(limit, tags.length)} max={tags.length} onChange={setLimit} />
          </div>
          <TagCloud tags={shownTags} selected={selected} onSelect={onSelect} minPx={11} maxPx={22} />
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
          {items.length === 0 ? (
            <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:tagsEmpty")}</p>
          ) : (
            <ul className="divide-y dark:divide-gray-800">
              {items.map((r) => (
                <RecordingRow
                  key={r.id}
                  r={r}
                  indentClass="pl-3"
                  selectMode={false}
                  selected={false}
                  onToggleSelect={() => {}}
                  onDropBefore={() => {}}
                  showDate
                  onNavigate={onClose}
                />
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}
