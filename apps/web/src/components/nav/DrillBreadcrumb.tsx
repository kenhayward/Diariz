import { useTranslation } from "react-i18next";
import { breadcrumbOf } from "../../lib/drillView";
import { useDrillSearch } from "../../lib/drillRoute";
import { sectionColor } from "../../lib/sectionColors";
import { ArrowLeftIcon, FolderIcon } from "../icons";
import FolderPath from "./FolderPath";
import type { SectionDto } from "../../lib/types";

/// The drill-in list's header row: a back button, the folder path, and a menu carrying the full ancestor
/// chain plus a link to the folder's own page.
///
/// Clicking a crumb and "Open section page" are deliberately **distinct targets** and must stay that way: a
/// crumb browses to that level (`onDrill`), while "Open section page" navigates the middle panel to the
/// folder itself. Collapsing them would make it impossible to reach a folder's page once you had drilled in.
///
/// Renders nothing at the room's top level - there is nowhere to go back to, and no page to open.
export default function DrillBreadcrumb({
  sections,
  sectionId,
  basePath,
  onDrill,
  onRecordingDrop,
}: {
  sections: SectionDto[];
  sectionId: string | null;
  basePath: string;
  onDrill: (sectionId: string | null) => void;
  /// A recording dragged onto an ancestor crumb - moves it up without engaging a modal.
  onRecordingDrop?: (sectionId: string, recordingId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const drillSearch = useDrillSearch();
  if (sectionId === null) return null;

  const chain = breadcrumbOf(sections, sectionId);
  const current = chain[chain.length - 1];
  // An unknown id (the folder was deleted while we were inside it) still renders the row, so the back
  // button remains a way out rather than stranding the user in an empty list.
  const parent = chain.length > 1 ? chain[chain.length - 2] : null;
  const color = sectionColor(sectionId);

  return (
    <div className="flex items-center gap-1.5 border-b px-2 py-1 dark:border-gray-800">
      <button
        type="button"
        aria-label={t("drillBack")}
        onClick={() => onDrill(parent?.id ?? null)}
        className="shrink-0 rounded border p-1 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
      >
        <ArrowLeftIcon size={14} />
      </button>

      <span
        style={{ "--sc-light": color.light, "--sc-dark": color.dark } as React.CSSProperties}
        className="shrink-0 text-[var(--sc-light)] dark:text-[var(--sc-dark)]"
      >
        <FolderIcon size={14} />
      </span>

      {/* Clicking a crumb DRILLS to that level; "Open section page" (in the menu) navigates the middle
          panel. Those stay distinct targets - collapsing them would make a folder's page unreachable once
          you had drilled into it. The page link carries `?in=` so opening it does not pop the list home. */}
      <FolderPath
        crumbs={chain.map((s) => ({ id: s.id, name: s.name }))}
        maxVisible={2}
        onSelect={(id) => onDrill(id)}
        onCrumbDrop={onRecordingDrop}
        extraItems={
          current
            ? [
                {
                  label: t("drillOpenSectionPage"),
                  to: { pathname: `${basePath}/sections/${current.id}`, search: drillSearch },
                },
              ]
            : []
        }
      />
    </div>
  );
}
