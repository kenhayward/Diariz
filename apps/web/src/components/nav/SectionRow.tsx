import { useState } from "react";
import { useMatch } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useRoomBasePath, useSharedRoomId } from "../../lib/rooms";
import { sectionColor } from "../../lib/sectionColors";
import { SECTION_MIME } from "../../lib/dragTypes";
import { ChevronRightIcon, FolderIcon } from "../icons";
import KebabMenu from "../KebabMenu";

/// A folder row in the drill-in list: coloured folder glyph, name, the count of everything underneath,
/// and a chevron saying "there is more this way".
///
/// The row body **drills in** - it does not open the folder's page. That is the design's rule: the row
/// browses deeper, and the breadcrumb's "Open section page" opens the page. Before the drill-in list the
/// name was a link to the page and a separate chevron collapsed the group; both of those are gone.
///
/// Keeps everything the old section heading carried: the kebab (rename / new sub-section / delete), the
/// drag source, and the drop targets for reordering and reparenting.
export default function SectionRow({
  id,
  name,
  count,
  canNest,
  onDrill,
  onSectionDropBefore,
  onSectionDropNest,
  onRecordingDrop,
}: {
  id: string;
  name: string;
  /// Everything underneath, including sub-folders' recordings - the row promises what you'll find inside.
  count: number;
  /// Whether this folder may take sub-folders (the domain caps the hierarchy at two levels), which decides
  /// both the "New sub-section" action and what a dropped folder does here.
  canNest: boolean;
  onDrill: () => void;
  onSectionDropBefore: (draggedSectionId: string) => void;
  onSectionDropNest: (draggedSectionId: string) => void;
  onRecordingDrop: (draggedRecordingId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const basePath = useRoomBasePath();
  const sharedRoomId = useSharedRoomId();
  const [renaming, setRenaming] = useState(false);
  // Highlighted while its own page is open in the middle panel - you can be reading a folder's page and
  // browsing elsewhere, so this is not the same thing as the drill position.
  const active = useMatch(`${basePath}/sections/:id`)?.params.id === id;
  const color = sectionColor(id);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["recordings"] });
    qc.invalidateQueries({ queryKey: ["sections"] });
  };

  async function save(newName: string) {
    const n = newName.trim();
    if (n && n !== name) {
      await api.renameSection(id, n);
      refresh();
    }
    setRenaming(false);
  }

  const actions = [
    { label: t("recordings:rename"), onClick: () => setRenaming(true) },
    ...(canNest
      ? [
          {
            label: t("newSubSection"),
            onClick: async () => {
              const sub = window.prompt(t("newSubSectionPlaceholder", { parent: name }))?.trim();
              if (!sub) return;
              await api.createSection(sub, id, sharedRoomId);
              refresh();
            },
          },
        ]
      : []),
    {
      label: t("recordings:delete"),
      danger: true,
      onClick: async () => {
        if (!window.confirm(t("confirmDeleteSection", { name }))) return;
        await api.deleteSection(id);
        refresh();
      },
    },
  ];

  return (
    <div
      className={`flex items-center gap-1 border-b py-1.5 pl-2 pr-1 dark:border-gray-800 ${
        active
          ? "bg-blue-50 dark:bg-blue-900/30"
          : "hover:bg-gray-50 dark:hover:bg-gray-800/60"
      }`}
      draggable={!renaming}
      onDragStart={(e) => {
        e.dataTransfer.setData(SECTION_MIME, id);
        e.dataTransfer.effectAllowed = "move";
      }}
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        const draggedSection = e.dataTransfer.getData(SECTION_MIME);
        if (draggedSection) {
          e.stopPropagation();
          // Onto a folder that can hold children: nest it. Onto one that can't: reorder before it.
          if (canNest) onSectionDropNest(draggedSection);
          else onSectionDropBefore(draggedSection);
          return;
        }
        const dragged = e.dataTransfer.getData("text/plain");
        if (dragged) {
          e.stopPropagation();
          onRecordingDrop(dragged);
        }
      }}
    >
      {renaming ? (
        <SectionRenameForm initial={name} onSave={save} onCancel={() => setRenaming(false)} />
      ) : (
        <>
          {/* The section's colour differs per theme, so it rides in as CSS custom properties and the
              dark: variant picks the arm. One text node, not a light/dark pair — duplicating the name in
              the DOM would have a screen reader read every folder twice. The glyph inherits currentColor. */}
          <button
            type="button"
            onClick={onDrill}
            aria-label={t("drillOpenFolder", { name })}
            style={{ "--sc-light": color.light, "--sc-dark": color.dark } as React.CSSProperties}
            className="flex min-w-0 flex-1 items-center gap-1.5 text-left text-[var(--sc-light)] dark:text-[var(--sc-dark)]"
            draggable={false}
          >
            <span className="shrink-0">
              <FolderIcon size={14} />
            </span>
            <span className="min-w-0 flex-1 truncate text-[12.5px] font-semibold">{name}</span>
            <span className="shrink-0 text-[11px] tabular-nums text-gray-400 dark:text-gray-500">{count}</span>
            <span className="shrink-0 text-gray-400 dark:text-gray-600">
              <ChevronRightIcon size={14} />
            </span>
          </button>
          <KebabMenu actions={actions} label={t("sectionActions")} />
        </>
      )}
    </div>
  );
}

function SectionRenameForm({
  initial,
  onSave,
  onCancel,
}: {
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  return (
    <form
      className="flex min-w-0 flex-1 items-center gap-1"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(value);
      }}
    >
      <input
        autoFocus
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        aria-label={t("sectionNameAria")}
        className="min-w-0 flex-1 rounded border px-2 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button
        type="submit"
        className="rounded border px-2 py-0.5 text-xs hover:bg-white/50 dark:border-gray-700 dark:hover:bg-gray-800"
      >
        {t("common:save")}
      </button>
    </form>
  );
}
