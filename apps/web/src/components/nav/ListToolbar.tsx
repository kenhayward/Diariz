import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import { useSelection } from "../../lib/selection";
import { useMoveClipboard } from "../../lib/moveClipboard";
import { inDisplayOrder } from "../../lib/reorder";
import { sectionCreateTarget } from "../../lib/drillView";
import ToolbarButton, { iconProps } from "../ToolbarButton";
import type { RecordingSummary, SectionDto } from "../../lib/types";

/// Top-of-list toolbar: create a section (group), toggle multi-select, bulk-delete audio for the
/// selection, and refresh the list (picks up changes made on another machine/browser).
function ListToolbar({
  recordings,
  listMode,
  allowFolders,
  sections,
  drillSectionId,
  roomId,
  onError,
}: {
  recordings: RecordingSummary[];
  listMode: boolean;
  // Shown when the caller can manage the current room's contents (folders are per-room).
  allowFolders: boolean;
  // The room's folders and where the list is drilled to - together they decide where the folder button
  // creates: at the top level from the root, inside the folder you are browsing from one level in.
  sections: SectionDto[];
  drillSectionId: string | null;
  // The room to create sections in (a shared room, or undefined for the personal room).
  roomId?: string | null;
  onError: (msg: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { selectMode, setSelectMode, selectedIds, clear } = useSelection();
  const { cutRecordings } = useMoveClipboard();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);

  // Creating a folder while browsing inside one should land it where you are looking, so the button's
  // parent, label and placeholder all follow the drill. `blocked` means the drill is at the depth cap, or
  // the folder id is no longer in the tree (see `sectionCreateTarget`): the button stays visible but
  // disabled, saying why, rather than quietly creating the folder at some other level.
  const target = sectionCreateTarget(sections, drillSectionId);
  const parentId = target.kind === "child" ? target.parent.id : null;
  const createLabel =
    target.kind === "blocked"
      ? t("newSectionNestCapped")
      : target.kind === "child"
        ? t("newSubSection")
        : t("newSection");
  const createPlaceholder =
    target.kind === "child"
      ? t("newSubSectionPlaceholder", { parent: target.parent.name })
      : t("newSectionPlaceholder");

  async function create(e: React.FormEvent) {
    e.preventDefault();
    const n = name.trim();
    if (!n) return;
    setBusy(true);
    try {
      await api.createSection(n, parentId, roomId);
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      setName("");
      setOpen(false);
    } catch (e) {
      onError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  // Of the selected recordings, those that still have audio to delete.
  const selectedWithAudio = recordings.filter((r) => selectedIds.includes(r.id) && r.hasAudio);

  async function deleteSelectedAudio() {
    const ids = selectedWithAudio.map((r) => r.id);
    if (ids.length === 0) return;
    if (!window.confirm(t("confirmDeleteAudioBulk", { count: ids.length }))) return;
    onError(null);
    try {
      await api.deleteAudioBulk(ids);
      clear();
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["user-storage"] }); // freed quota → refresh the account menu
    } catch (e) {
      onError(apiErrorMessage(e));
    }
  }

  async function mergeSelected() {
    if (selectedIds.length < 2) return;
    if (!window.confirm(t("confirmMergeRecordings", { count: selectedIds.length }))) return;
    onError(null);
    try {
      await api.mergeRecordings(selectedIds);
      clear();
      qc.invalidateQueries({ queryKey: ["recordings"] }); // survivor flips to "Merging" until the worker finishes
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    } catch (e) {
      onError(apiErrorMessage(e));
    }
  }

  // Stages the selection on the move clipboard - nothing touches the server here. The source recorded is
  // this drill level (not the selected recordings' own sectionId, which would be the same thing at this
  // level anyway) so a later paste onto the same folder can be detected and disabled.
  //
  // selectedIds is in TICK order (checkbox toggles append), not display order - ticking the third row then
  // the first would otherwise cut them third-then-first. The product decision is that a paste "preserves
  // relative order", which has to mean the order the rows are shown in, not the order they were clicked -
  // so re-sort into `recordings`' own order (the API already returns it in display/position order, the
  // same order the rows render in) before it ever reaches the clipboard.
  function cutSelected() {
    if (selectedIds.length === 0) return;
    cutRecordings(inDisplayOrder(selectedIds, recordings.map((r) => r.id)), drillSectionId, roomId ?? null);
  }

  return (
    <div className="flex h-9 items-center justify-between gap-2 border-b px-3 dark:border-gray-700">
      {open ? (
        <form onSubmit={create} className="flex min-w-0 flex-1 items-center gap-1">
          <input
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setOpen(false)}
            placeholder={createPlaceholder}
            aria-label={createPlaceholder}
            className="min-w-0 flex-1 rounded border px-2 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={busy || !name.trim()}
            className="rounded border px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:create")}
          </button>
        </form>
      ) : (
        <div className="flex items-center gap-0.5">
          {/* New Section / Select / bulk actions apply to the List tab only; Refresh works in both. */}
          {allowFolders && (
            // The title lives on the wrapper, not the button: ToolbarButton disables pointer events when
            // disabled, so a title on the button itself would never surface the reason it is greyed out.
            <span title={createLabel}>
              <ToolbarButton
                label={createLabel}
                onClick={() => setOpen(true)}
                disabled={!listMode || target.kind === "blocked"}
                icon={<FolderPlusIcon />}
              />
            </span>
          )}
          <ToolbarButton
            label={selectMode ? t("doneSelecting") : t("selectRecordings")}
            onClick={() => setSelectMode(!selectMode)}
            active={selectMode}
            disabled={!listMode}
            icon={<SelectIcon />}
          />
          {selectMode && (
            <ToolbarButton
              label={t("mergeTranscripts")}
              onClick={mergeSelected}
              disabled={!listMode || selectedIds.length < 2}
              icon={<MergeIcon />}
            />
          )}
          {selectMode && (
            <ToolbarButton
              label={t("recordings:deleteAudio")}
              onClick={deleteSelectedAudio}
              disabled={!listMode || selectedWithAudio.length === 0}
              icon={<TrashIcon />}
            />
          )}
          {selectMode && (
            // Disabled in a shared room, with the reason stated. Pasting INTO a shared room is blocked, and
            // so is pasting a shared-room cut anywhere else - so a cut staged here would have nowhere at all
            // to go, and offering it would be a trap. The title rides on the wrapper, not the button:
            // ToolbarButton drops pointer events when disabled, so a title on the button itself would never
            // surface (same reason the New section button wraps its own).
            <span title={roomId != null ? t("cutSharedRoomBlocked") : undefined}>
              <ToolbarButton
                label={t("cut")}
                onClick={cutSelected}
                disabled={!listMode || selectedIds.length === 0 || roomId != null}
                icon={<CutIcon />}
              />
            </span>
          )}
          <ToolbarButton
            label={t("refresh")}
            onClick={() => {
              qc.invalidateQueries({ queryKey: ["recordings"] });
              qc.invalidateQueries({ queryKey: ["sections"] });
            }}
            icon={<RefreshIcon />}
          />
          {selectMode && selectedIds.length > 0 && (
            <span className="text-xs text-blue-700 dark:text-blue-300">{selectedIds.length}</span>
          )}
        </div>
      )}
    </div>
  );
}

const FolderPlusIcon = () => (
  <svg {...iconProps}>
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
    <line x1="12" y1="11" x2="12" y2="17" />
    <line x1="9" y1="14" x2="15" y2="14" />
  </svg>
);
const SelectIcon = () => (
  <svg {...iconProps}>
    <path d="M9 11l3 3L22 4" />
    <path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
  </svg>
);
const RefreshIcon = () => (
  <svg {...iconProps}>
    <path d="M23 4v6h-6" />
    <path d="M1 20v-6h6" />
    <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
  </svg>
);
const TrashIcon = () => (
  <svg {...iconProps}>
    <polyline points="3 6 5 6 21 6" />
    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
  </svg>
);
const MergeIcon = () => (
  <svg {...iconProps}>
    <path d="M6 3v6a6 6 0 0 0 6 6 6 6 0 0 0 6-6V3" />
    <line x1="12" y1="15" x2="12" y2="21" />
  </svg>
);
const CutIcon = () => (
  <svg {...iconProps}>
    <circle cx="6" cy="6" r="3" />
    <circle cx="6" cy="18" r="3" />
    <line x1="20" y1="4" x2="8.12" y2="15.88" />
    <line x1="14.47" y1="14.48" x2="20" y2="20" />
    <line x1="8.12" y1="8.12" x2="12" y2="12" />
  </svg>
);

export default ListToolbar;
