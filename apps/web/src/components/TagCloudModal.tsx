import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import TagCloud from "./TagCloud";
import { recordingsForTags } from "../lib/tagCloud";
import { formatDuration } from "../lib/format";
import type { RecordingSummary, TagCloudEntry } from "../lib/types";

/// The expanded tag-cloud view: a large modal (~80% of the page) with a bigger cloud on top and the
/// matching recordings below. The tag selection is the PARENT's state (shared with the left panel's
/// cloud), so picking a tag here filters the panel behind too and survives closing. Clicking a recording
/// closes the modal and opens it; ✕/Escape close without navigating (no outside-click close - repo
/// convention). Mirrors the ManageMeetingTypesModal shell.
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
  const navigate = useNavigate();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const items = recordingsForTags(recordings, tags, selected);

  function open(id: string) {
    onClose();
    navigate(`/recordings/${id}`);
  }

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

        <div className="shrink-0 overflow-y-auto border-b px-4 py-2 dark:border-gray-700" style={{ maxHeight: "45%" }}>
          <TagCloud tags={tags} selected={selected} onSelect={onSelect} minPx={16} maxPx={48} />
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
          {items.length === 0 ? (
            <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:tagsEmpty")}</p>
          ) : (
            <ul className="divide-y dark:divide-gray-800">
              {items.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    onClick={() => open(r.id)}
                    className="flex w-full items-center gap-2 px-4 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800"
                  >
                    <span className="min-w-0 flex-1 truncate dark:text-gray-100">{r.name ?? r.title}</span>
                    <span className="shrink-0 text-xs text-gray-400 dark:text-gray-500">
                      {formatDuration(r.durationMs)}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}
