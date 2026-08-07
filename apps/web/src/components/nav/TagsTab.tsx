import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useSelection } from "../../lib/selection";
import { recordingsForTags, topTagsByCount } from "../../lib/tagCloud";
import { iconProps } from "../ToolbarButton";
import TagCloud from "../TagCloud";
import TagCloudModal from "../TagCloudModal";
import { RecordingRow } from "./RecordingRow";
import { TagCountSlider } from "./TagCountSlider";
import type { RecordingSummary } from "../../lib/types";

const TAG_LIMIT_KEY = "diariz.recordings.tagLimit";
const DEFAULT_TAG_LIMIT = 40;

/// How many tags the cloud shows, from the last session. Guarded like `lib/panelTab.ts`: storage can be
/// disabled outright (private browsing, a locked-down profile) and throw on access. This is read from a
/// `useState` initialiser, so an unguarded throw takes down this tab's whole render.
function storedTagLimit(): number {
  try {
    return Number(localStorage.getItem(TAG_LIMIT_KEY)) || DEFAULT_TAG_LIMIT;
  } catch {
    return DEFAULT_TAG_LIMIT;
  }
}

/// The panel's Tags tab: the aggregated weighted cloud over the recordings carrying a shown tag, with one
/// tag selectable to filter the list. The selection is shared with the expanded modal so the tab always
/// mirrors what was picked there.
///
/// Owns its own state and query. The panel mounts this only while the tab is showing, so the query does not
/// run from the other tabs - and the picked tag clears when you leave and come back. That is arguably a
/// correction as much as a change: a held tag goes stale when its recordings are deleted or re-tagged,
/// which is why the effect below exists. Pinned by TagsTab.test.tsx.
export default function TagsTab({
  recordings,
  roomId,
}: {
  recordings: RecordingSummary[];
  /// The room to aggregate tags for - undefined for the personal library (the owner-scoped path).
  roomId: string | undefined;
}) {
  const { t } = useTranslation("workspace");
  const selection = useSelection();
  const { data: tags = [] } = useQuery({
    queryKey: ["tags", roomId ?? null],
    queryFn: () => api.listTags(roomId),
  });
  const [selectedTag, setSelectedTag] = useState<string | null>(null);
  const [tagCloudExpanded, setTagCloudExpanded] = useState(false);
  // Count slider: how many tags to show (the most-used first). Persisted; clamped to what's available.
  const [tagLimit, setTagLimit] = useState<number>(storedTagLimit);
  function setTagLimitPersisted(n: number) {
    try {
      localStorage.setItem(TAG_LIMIT_KEY, String(n));
    } catch {
      /* storage disabled: the slider still moves this session, it just won't be remembered */
    }
    setTagLimit(n);
  }
  // A refetch can drop the selected tag (recording deleted / re-tagged) - clear a stale selection so the
  // list doesn't silently show "nothing" for a tag that no longer exists.
  useEffect(() => {
    if (selectedTag && tags.length > 0 && !tags.some((x) => x.tag === selectedTag)) setSelectedTag(null);
  }, [tags, selectedTag]);
  const shownTags = useMemo(() => topTagsByCount(tags, tagLimit), [tags, tagLimit]);
  // The list follows the shown tags: with no tag picked it's every recording carrying a *shown* tag.
  const tagItems = useMemo(
    () => recordingsForTags(recordings, shownTags, selectedTag),
    [recordings, shownTags, selectedTag],
  );

  return (
    // Tags: the weighted cloud stays fixed at the top (like the calendar's month grid); only the
    // matching-recordings list below it scrolls. min-w-0 for the same truncation reason as calendar.
    <div className="flex min-h-0 min-w-0 flex-1 flex-col">
      {tags.length === 0 ? (
        <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("tagsEmpty")}</p>
      ) : (
        <>
          {/* The cloud + its count slider stay fixed at the top; the cloud is height-capped and scrolls
              internally so the recordings list below is always visible however many tags there are. */}
          <div className="shrink-0 border-b dark:border-gray-800">
            <div className="flex items-center gap-2 px-3 pt-2">
              <TagCountSlider
                value={Math.min(tagLimit, tags.length)}
                max={tags.length}
                onChange={setTagLimitPersisted}
              />
              <button
                type="button"
                aria-label={t("tagCloudExpand")}
                title={t("tagCloudExpand")}
                onClick={() => setTagCloudExpanded(true)}
                className="ml-auto shrink-0 rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600 dark:text-gray-500 dark:hover:bg-gray-800 dark:hover:text-gray-300"
              >
                <svg {...iconProps}>
                  <path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7" />
                </svg>
              </button>
            </div>
            <div className="max-h-[38vh] overflow-y-auto">
              <TagCloud tags={shownTags} selected={selectedTag} onSelect={setSelectedTag} />
            </div>
          </div>
          <div className="min-h-0 min-w-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
            {tagItems.length > 0 && (
              <ul className="divide-y dark:divide-gray-800">
                {tagItems.map((r) => (
                  <RecordingRow
                    key={r.id}
                    r={r}
                    indentClass="pl-3"
                    selectMode={selection.selectMode}
                    selected={selection.selectedIds.includes(r.id)}
                    onToggleSelect={() => selection.toggle(r.id)}
                    onDropBefore={() => {}}
                    showDate
                  />
                ))}
              </ul>
            )}
          </div>
        </>
      )}
      {/* Rendered from inside the tab now rather than the panel's modal slot. It is `fixed inset-0 z-50`,
          so where it sits in the tree does not affect where it appears. */}
      {tagCloudExpanded && (
        <TagCloudModal
          tags={tags}
          recordings={recordings}
          selected={selectedTag}
          onSelect={setSelectedTag}
          onClose={() => setTagCloudExpanded(false)}
        />
      )}
    </div>
  );
}
