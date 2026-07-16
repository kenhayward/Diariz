import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { breadcrumbOf } from "../../lib/drillView";
import { useDrillSearch } from "../../lib/drillRoute";
import { sectionColor } from "../../lib/sectionColors";
import { ArrowLeftIcon, FolderIcon } from "../icons";
import type { SectionDto } from "../../lib/types";

/// The drill-in list's header row: a back button, the current folder over the parent you'd pop to, and a
/// link to the folder's own page.
///
/// Those last two are deliberately **distinct targets** and must stay that way: a folder row's body
/// browses deeper (`onDrill`), while "Open section page" navigates the middle panel to the folder itself.
/// Collapsing them would make it impossible to reach a folder's page once you'd drilled into it.
///
/// Renders nothing at the room's top level - there is nowhere to go back to, and no page to open.
export default function DrillBreadcrumb({
  sections,
  sectionId,
  basePath,
  onDrill,
}: {
  sections: SectionDto[];
  sectionId: string | null;
  basePath: string;
  onDrill: (sectionId: string | null) => void;
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
    <div className="flex items-start gap-2 border-b px-2 py-2 dark:border-gray-800">
      <button
        type="button"
        aria-label={t("drillBack")}
        onClick={() => onDrill(parent?.id ?? null)}
        className="mt-0.5 shrink-0 rounded border p-1 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
      >
        <ArrowLeftIcon size={14} />
      </button>

      <div className="min-w-0 flex-1">
        <p className="truncate text-[11px] leading-tight text-gray-500 dark:text-gray-400">
          {parent ? parent.name : t("drillAllSections")}
        </p>
        <p className="flex min-w-0 items-center gap-1 text-[13.5px] font-semibold leading-tight text-gray-900 dark:text-gray-100">
          {/* Only the glyph is section-coloured here; the name stays foreground text, since at 13.5px
              semibold the palette colour on white fails contrast. */}
          <span
            style={{ "--sc-light": color.light, "--sc-dark": color.dark } as React.CSSProperties}
            className="shrink-0 text-[var(--sc-light)] dark:text-[var(--sc-dark)]"
          >
            <FolderIcon size={14} />
          </span>
          <span className="truncate">{current?.name ?? ""}</span>
        </p>
      </div>

      {current && (
        // Carry the drill across: a bare path would drop `?in=`, popping the list back to the root
        // behind the very page you just opened. Opening a folder's page and browsing it are independent
        // - that is the whole point of keeping them as separate targets.
        <Link
          to={{ pathname: `${basePath}/sections/${current.id}`, search: drillSearch }}
          className="mt-0.5 shrink-0 text-[11px] text-blue-600 hover:underline dark:text-blue-400"
        >
          {t("drillOpenSectionPage")}
        </Link>
      )}
    </div>
  );
}
