import { useTranslation } from "react-i18next";
import { collapsePath, type PathCrumb } from "../../lib/folderPath";
import { ChevronRightIcon } from "../icons";

/// Where a recording is filed, as a row of navigable chips under its name on the detail page.
///
/// Presentational: it takes the room name, the folder chain and one callback, and knows nothing about
/// drilling, rooms or tabs. The caller decides what selecting a chip means.
///
/// Two deliberate differences from the nav's `FolderPath`, which renders the same data:
///
///  * **Every chip is a control, including the last.** `FolderPath`'s last crumb is static because it is
///    the folder you are already looking at, so clicking it would be a no-op that still pushed history.
///    Here the last chip names the folder the *recording* sits in while the reader is on the recording
///    page - it is the most likely click of the whole row, not a no-op.
///  * **The room is a chip, not an implied root.** A recording filed at the top level still gets a row (one
///    chip) rather than the affordance vanishing, and the room chip is the one-click way back to the top of
///    the list from anywhere.
///
/// Collapsing is `collapsePath`, shared with the nav, so a deep path gives way the same way in both places.
/// The room chip sits outside that budget: it anchors the row and always survives.
export default function FolderChips({
  roomName,
  crumbs,
  onSelect,
  maxVisible = 3,
}: {
  roomName: string;
  /// The folder chain within `roomName`, outermost first. Empty = filed at the room's top level.
  crumbs: PathCrumb[];
  /// A chip was chosen: a folder id, or null for the room itself (its top level).
  onSelect: (sectionId: string | null) => void;
  /// How many folder chips may show before the middle collapses. Excludes the room chip.
  maxVisible?: number;
}) {
  const { t } = useTranslation("workspace");
  const segments = collapsePath(crumbs, maxVisible);

  return (
    <nav aria-label={t("folderChipsLabel")} className="-mt-0.5 flex flex-wrap items-center gap-1">
      <Chip label={roomName} onClick={() => onSelect(null)} />
      {segments.map((seg, i) => (
        <span key={seg === "ellipsis" ? `gap-${i}` : seg.id} className="flex min-w-0 items-center gap-1">
          <span className="shrink-0 text-gray-300 dark:text-gray-600" aria-hidden>
            <ChevronRightIcon size={11} />
          </span>
          {seg === "ellipsis" ? (
            // An indicator, not a control: the chips it hides are reachable by clicking any visible
            // ancestor and drilling, and a second control here would only crowd the row.
            <span className="shrink-0 px-1 text-xs text-gray-400 dark:text-gray-500" title={t("folderPathCollapsed")}>
              &hellip;
            </span>
          ) : (
            <Chip label={seg.name} onClick={() => onSelect(seg.id)} />
          )}
        </span>
      ))}
    </nav>
  );
}

function Chip({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="max-w-[14rem] truncate rounded-full border border-gray-200 bg-gray-50 px-2.5 py-0.5 text-xs text-gray-700 hover:border-blue-300 hover:bg-blue-50 hover:text-blue-700 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-200 dark:hover:border-blue-500 dark:hover:bg-blue-900/30 dark:hover:text-blue-300"
    >
      {label}
    </button>
  );
}
