import { useEffect, useMemo, useRef, useState } from "react";
import { NavLink } from "react-router-dom";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "./KebabMenu";
import ToolbarButton, { iconProps } from "./ToolbarButton";
import MoveToSectionModal from "./MoveToSectionModal";
import DownloadTranscriptModal from "./DownloadTranscriptModal";
import { recordingMenu } from "./recordingMenu";
import { copyRichLink, transcriptUrl } from "../lib/clipboard";
import { useSelection } from "../lib/selection";
import { formatDuration } from "../lib/format";
import { computeReorder } from "../lib/reorder";
import { useDragAutoScroll } from "../lib/dragAutoScroll";
import { buildRecordingTree, reorderBeforeSection, appendSectionUnder, type SectionNode } from "../lib/recordingTree";
import MonthCalendar from "./MonthCalendar";
import { recordingDayKeys, recordingsForDay, dayKey } from "../lib/calendar";
import { useUpload } from "../lib/uploadContext";
import { distinctActors, filterActions } from "../lib/actionsView";
import ActionsToolbar from "./ActionsToolbar";
import ActionsTab from "./ActionsTab";
import EditActionModal from "./EditActionModal";
import type { UploadItem } from "../lib/uploadQueue";
import type { ActionListItem, RecordingStatus, RecordingSource, RecordingSummary } from "../lib/types";

const dragHasFiles = (e: React.DragEvent) => Array.from(e.dataTransfer.types ?? []).includes("Files");

const statusColor: Record<RecordingStatus, string> = {
  Uploaded: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  Queued: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribed: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Summarizing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Summarized: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Merging: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Failed: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
};

const COLLAPSE_KEY = "diariz.recordings.collapsedGroups";
const UNGROUPED_KEY = "__ungrouped__";
const TAB_KEY = "diariz.recordings.tab";
type PanelTab = "list" | "calendar" | "actions";
/// Drag payload type marking a section-header drag (vs a recording's "text/plain" id or a file drop).
const SECTION_MIME = "application/x-diariz-section";

function sourceLabel(s: RecordingSource, t: TFunction): string {
  if (s === "System") return t("workspace:sourceSystem");
  if (s === "Upload") return t("workspace:sourceUpload");
  return t("workspace:sourceMicrophone");
}

export function hasTranscript(status: RecordingStatus): boolean {
  return status === "Transcribed" || status === "Summarizing" || status === "Summarized";
}

/// Show the status pill only while the pipeline is moving. The settled success states
/// (Transcribed/Summarized) repeat on every row and truncate the name, so they're hidden.
export function showStatusBadge(status: RecordingStatus): boolean {
  return status !== "Transcribed" && status !== "Summarized";
}

/// The recordings list for the left panel, grouped into user sections (Ungrouped last).
/// Selecting a row routes to /recordings/:id (middle panel).
export default function RecordingsPanel() {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { data: recordings = [], isLoading } = useQuery({
    queryKey: ["recordings"],
    queryFn: api.listRecordings,
  });
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: api.listSections });

  useEffect(() => {
    const hub = createHub(() => qc.invalidateQueries({ queryKey: ["recordings"] }));
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  const tree = useMemo(() => buildRecordingTree(recordings, sections), [recordings, sections]);
  const selection = useSelection();
  const [opError, setOpError] = useState<string | null>(null);

  // List vs Calendar tab (persisted). The calendar shows the month, focused on today, and lists the
  // selected day's recordings below it.
  const [tab, setTab] = useState<PanelTab>(() => {
    const v = localStorage.getItem(TAB_KEY);
    return v === "calendar" || v === "actions" ? v : "list";
  });
  function selectTab(next: PanelTab) {
    localStorage.setItem(TAB_KEY, next);
    // Selection is per-domain (recordings vs actions) — never carry it across a tab switch.
    selection.clear();
    selection.setSelectMode(false);
    setTab(next);
  }

  // Actions tab: all actions across the library, filtered by person + hide-complete, with one open editor.
  const { data: allActions = [] } = useQuery({
    queryKey: ["actions", "all"],
    queryFn: api.listAllActions,
    enabled: tab === "actions",
  });
  const [personFilter, setPersonFilter] = useState<string | null>(null);
  const [hideComplete, setHideComplete] = useState(false);
  const [editingAction, setEditingAction] = useState<ActionListItem | null>(null);
  const persons = useMemo(() => distinctActors(allActions), [allActions]);
  const visibleActions = useMemo(
    () => filterActions(allActions, { person: personFilter, hideComplete }),
    [allActions, personFilter, hideComplete],
  );
  const [month, setMonth] = useState(() => {
    const now = new Date();
    return { year: now.getFullYear(), month: now.getMonth() };
  });
  const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
  const dayKeys = useMemo(() => recordingDayKeys(recordings), [recordings]);
  const dayRecordings = selectedDay ? recordingsForDay(recordings, selectedDay) : [];
  function stepMonth(delta: number) {
    setMonth((m) => {
      const d = new Date(m.year, m.month + delta, 1);
      return { year: d.getFullYear(), month: d.getMonth() };
    });
  }
  const [collapsed, setCollapsed] = useState<Set<string>>(() => {
    try {
      return new Set<string>(JSON.parse(localStorage.getItem(COLLAPSE_KEY) ?? "[]"));
    } catch {
      return new Set<string>();
    }
  });
  function toggleGroup(key: string) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      localStorage.setItem(COLLAPSE_KEY, JSON.stringify([...next]));
      return next;
    });
  }

  /// Apply a drag-and-drop: set the dragged recording's group + order, then refresh.
  async function drop(sectionId: string | null, groupIds: string[], draggedId: string, beforeId: string | null) {
    if (!draggedId) return;
    await api.reorderRecordings(sectionId, computeReorder(groupIds, draggedId, beforeId));
    qc.invalidateQueries({ queryKey: ["recordings"] });
  }

  /// Section drag-and-drop. The server may reject a move that would nest more than one level deep
  /// (e.g. a section with sub-sections dropped under another) — surface that in the banner.
  async function runSection(fn: () => Promise<unknown>) {
    setOpError(null);
    try {
      await fn();
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
    } catch (e) {
      setOpError(apiErrorMessage(e));
    }
  }
  /// Drop a section header onto another section header: reorder before it (adopting its level/parent).
  function dropSectionBefore(targetId: string, draggedId: string) {
    if (!draggedId || draggedId === targetId) return;
    const payload = reorderBeforeSection(sections, draggedId, targetId);
    if (payload) runSection(() => api.reorderSections(payload.parentId, payload.orderedIds));
  }
  /// Drop a section into a top-level section's body (nest it) or onto the Ungrouped bar (promote to top).
  function nestSection(parentId: string | null, draggedId: string) {
    if (!draggedId || draggedId === parentId) return;
    const payload = appendSectionUnder(sections, draggedId, parentId);
    runSection(() => api.reorderSections(payload.parentId, payload.orderedIds));
  }

  // Drag audio files anywhere onto the panel to upload them (distinct from the reorder DnD, which uses
  // the "text/plain" payload — file drags carry "Files"). A depth counter keeps the highlight stable as
  // the cursor moves over child rows.
  const upload = useUpload();
  const [dragging, setDragging] = useState(false);
  const dragDepth = useRef(0);
  // Native HTML5 DnD doesn't scroll the list while dragging near its edges, so a drop target outside the
  // viewport is unreachable in a long tree. These auto-scroll the list / day-list during any drag.
  const listScrollRef = useDragAutoScroll<HTMLDivElement>();
  const dayScrollRef = useDragAutoScroll<HTMLDivElement>();
  function onFileDragEnter(e: React.DragEvent) {
    if (!dragHasFiles(e)) return;
    dragDepth.current += 1;
    setDragging(true);
  }
  function onFileDragLeave(e: React.DragEvent) {
    if (!dragHasFiles(e)) return;
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  }
  function onFileDragOver(e: React.DragEvent) {
    if (dragHasFiles(e)) e.preventDefault(); // allow drop
  }
  function onFileDrop(e: React.DragEvent) {
    if (!dragHasFiles(e)) return; // a reorder drop — leave it to the row/group handlers
    e.preventDefault();
    setDragging(false);
    dragDepth.current = 0;
    upload.uploadFiles(Array.from(e.dataTransfer.files));
  }

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;

  // Show section headings whenever any (real or just-created) section exists; a flat list otherwise.
  const showHeadings = tree.sections.length > 0;

  // Select mode: a checkbox that selects/deselects every recording directly in this section at once.
  const selectAllFor = (node: SectionNode) => {
    const ids = node.items.map((i) => i.id);
    if (!selection.selectMode || ids.length === 0) return undefined;
    return (
      <GroupSelectCheckbox
        groupName={node.name}
        ids={ids}
        selectedIds={selection.selectedIds}
        onChange={(checkAll) => {
          const next = new Set(selection.selectedIds);
          if (checkAll) ids.forEach((id) => next.add(id));
          else ids.forEach((id) => next.delete(id));
          selection.set([...next]);
        }}
      />
    );
  };

  const rowList = (sectionId: string | null, items: RecordingSummary[], indentClass = "pl-3") => {
    const ids = items.map((i) => i.id);
    return (
      <ul className="divide-y dark:divide-gray-800">
        {items.map((r) => (
          <RecordingRow
            key={r.id}
            r={r}
            indentClass={indentClass}
            selectMode={selection.selectMode}
            selected={selection.selectedIds.includes(r.id)}
            onToggleSelect={() => selection.toggle(r.id)}
            onDropBefore={(draggedId) => drop(sectionId, ids, draggedId, r.id)}
          />
        ))}
      </ul>
    );
  };

  // Render one section node (top-level or a sub-section). The wrapping div is the drop target: a section
  // payload nests the dragged section here (top-level only); a recording payload appends to this section.
  const renderSection = (node: SectionNode, nested: boolean): React.ReactNode => {
    const ids = node.items.map((i) => i.id);
    const isCollapsed = collapsed.has(node.id);
    return (
      <div
        key={node.id}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.stopPropagation();
          const draggedSection = e.dataTransfer.getData(SECTION_MIME);
          if (draggedSection) {
            if (!nested) nestSection(node.id, draggedSection); // nest a section into this top-level section
            return;
          }
          const dragged = e.dataTransfer.getData("text/plain");
          if (dragged) drop(node.id, ids, dragged, null);
        }}
      >
        {showHeadings && (
          <SectionHeading
            id={node.id}
            name={node.name}
            count={node.items.length}
            collapsed={isCollapsed}
            nested={nested}
            onToggle={() => toggleGroup(node.id)}
            leading={selectAllFor(node)}
            onSectionDropBefore={(draggedId) => dropSectionBefore(node.id, draggedId)}
            onSectionDropNest={(draggedId) => nestSection(node.id, draggedId)}
          />
        )}
        {!isCollapsed && (
          <>
            {/* Recordings sit slightly indented under their section heading; deeper under a sub-section. */}
            {node.items.length > 0 && rowList(node.id, node.items, nested ? "pl-10" : "pl-6")}
            {node.children.map((child) => renderSection(child, true))}
          </>
        )}
      </div>
    );
  };

  return (
    // Flex column so the toolbar stays pinned at the top while only the list below it scrolls (mirrors
    // the chat panel). The drop-zone ring sits on the outer frame.
    <div
      onDragEnter={onFileDragEnter}
      onDragLeave={onFileDragLeave}
      onDragOver={onFileDragOver}
      onDrop={onFileDrop}
      className={`flex h-full flex-col ${dragging ? "rounded-md ring-2 ring-inset ring-blue-400 dark:ring-blue-500" : ""}`}
    >
      {tab === "actions" ? (
        <ActionsToolbar
          actions={allActions}
          hideComplete={hideComplete}
          onToggleHideComplete={() => setHideComplete((v) => !v)}
          onEdit={() => {
            const sel = allActions.find((a) => selection.selectedIds.includes(a.id));
            if (sel) setEditingAction(sel);
          }}
          onError={setOpError}
        />
      ) : (
        <ListToolbar recordings={recordings} listMode={tab === "list"} onError={setOpError} />
      )}
      <div className="flex min-h-0 flex-1">
        <TabStrip tab={tab} onSelect={selectTab} />
        {tab === "list" ? (
          // min-w-0 lets this flex child shrink to the panel width so long recording names truncate
          // instead of forcing the column wider than the panel.
          <div ref={listScrollRef} className="min-h-0 min-w-0 flex-1 overflow-y-auto">
            <UploadStatusList items={upload.items} onClear={upload.clearFinished} />
            {dragging && (
              <p className="px-3 py-2 text-center text-xs font-medium text-blue-600 dark:text-blue-400">
                {t("dropToUpload")}
              </p>
            )}
            {recordings.length === 0 && !dragging && (
              <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("noRecordings")}</p>
            )}
            {opError && <p className="px-3 py-1 text-xs text-red-600 dark:text-red-400">{opError}</p>}
            {tree.sections.map((node) => renderSection(node, false))}
            {tree.ungrouped.length > 0 && (
              <div
                // Dropping a recording here ungroups it; dropping a section here promotes it to top-level.
                onDragOver={(e) => e.preventDefault()}
                onDrop={(e) => {
                  e.stopPropagation();
                  const draggedSection = e.dataTransfer.getData(SECTION_MIME);
                  if (draggedSection) {
                    nestSection(null, draggedSection);
                    return;
                  }
                  const dragged = e.dataTransfer.getData("text/plain");
                  if (dragged) drop(null, tree.ungrouped.map((i) => i.id), dragged, null);
                }}
              >
                {showHeadings && (
                  <GroupHeadingButton
                    name={t("ungrouped")}
                    count={tree.ungrouped.length}
                    collapsed={collapsed.has(UNGROUPED_KEY)}
                    onToggle={() => toggleGroup(UNGROUPED_KEY)}
                    withBg
                    leading={selectAllFor({ id: UNGROUPED_KEY, name: t("ungrouped"), items: tree.ungrouped, children: [] })}
                  />
                )}
                {!collapsed.has(UNGROUPED_KEY) && rowList(null, tree.ungrouped, showHeadings ? "pl-6" : "pl-3")}
              </div>
            )}
          </div>
        ) : tab === "calendar" ? (
          // Calendar: the month grid stays fixed at the top; only the selected day's list scrolls.
          // min-w-0 is essential: without it this flex child grows to the widest day-list row, which would
          // stretch the grid-cols-7 month grid wider than the panel and make the calendar appear to resize
          // when you pick a day with longer recording names.
          <div className="flex min-h-0 min-w-0 flex-1 flex-col">
            <div className="shrink-0 border-b dark:border-gray-800">
              <MonthCalendar
                year={month.year}
                month={month.month}
                daysWithRecordings={dayKeys}
                selectedKey={selectedDay}
                onSelect={setSelectedDay}
                onPrev={() => stepMonth(-1)}
                onNext={() => stepMonth(1)}
              />
            </div>
            {/* Reserve the scrollbar gutter so toggling the day list's scrollbar never shifts its width. */}
            <div ref={dayScrollRef} className="min-h-0 min-w-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
              {dayRecordings.length === 0 ? (
                <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("calNoRecordings")}</p>
              ) : (
                rowList(null, dayRecordings)
              )}
            </div>
          </div>
        ) : (
          // Actions: a flat, cross-transcript list with its own filter/select/complete toolbar above.
          <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">
            {opError && <p className="px-3 py-1 text-xs text-red-600 dark:text-red-400">{opError}</p>}
            <ActionsTab actions={visibleActions} persons={persons} person={personFilter} onPerson={setPersonFilter} />
          </div>
        )}
      </div>
      {editingAction && <EditActionModal action={editingAction} onClose={() => setEditingAction(null)} />}
    </div>
  );
}

/// Vertical List / Calendar / Actions tabs, sitting to the left of the panel's scroll area (below the toolbar).
function TabStrip({ tab, onSelect }: { tab: PanelTab; onSelect: (t: PanelTab) => void }) {
  const { t } = useTranslation("workspace");
  const item = (key: PanelTab, label: string) => (
    <button
      type="button"
      onClick={() => onSelect(key)}
      aria-pressed={tab === key}
      className={`w-full px-2 py-2 text-[11px] font-medium [writing-mode:vertical-rl] ${
        tab === key
          ? "bg-white text-blue-700 dark:bg-gray-900 dark:text-blue-300"
          : "text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
      }`}
    >
      {label}
    </button>
  );
  return (
    <div className="flex w-7 shrink-0 flex-col items-stretch border-r bg-gray-50 dark:border-gray-800 dark:bg-gray-950/40">
      {item("list", t("tabList"))}
      {item("calendar", t("tabCalendar"))}
      {item("actions", t("tabActions"))}
    </div>
  );
}

/// Top-of-list toolbar: create a section (group), toggle multi-select, bulk-delete audio for the
/// selection, and refresh the list (picks up changes made on another machine/browser).
function ListToolbar({
  recordings,
  listMode,
  onError,
}: {
  recordings: RecordingSummary[];
  listMode: boolean;
  onError: (msg: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { selectMode, setSelectMode, selectedIds, clear } = useSelection();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    const n = name.trim();
    if (!n) return;
    setBusy(true);
    try {
      await api.createSection(n);
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      setName("");
      setOpen(false);
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

  return (
    <div className="flex h-9 items-center justify-between gap-2 border-b px-3 dark:border-gray-700">
      {open ? (
        <form onSubmit={create} className="flex min-w-0 flex-1 items-center gap-1">
          <input
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setOpen(false)}
            placeholder={t("newSectionPlaceholder")}
            aria-label={t("newSectionPlaceholder")}
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
          <ToolbarButton label={t("newSection")} onClick={() => setOpen(true)} disabled={!listMode} icon={<FolderPlusIcon />} />
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

/// A small microphone glyph marking whether a recording still has its audio: green when present,
/// grey once the audio has been deleted. Sits at the start of the row, after the drag handle.
function MicIcon({ on, title }: { on: boolean; title: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      width={14}
      height={14}
      role="img"
      aria-label={title}
      className={`shrink-0 ${on ? "text-green-600 dark:text-green-400" : "text-gray-300 dark:text-gray-600"}`}
    >
      <title>{title}</title>
      <path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z" />
      <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
      <line x1="12" y1="19" x2="12" y2="23" />
      <line x1="8" y1="23" x2="16" y2="23" />
    </svg>
  );
}

/// Per-file status for the current upload batch (queued/uploading/done/failed). Tolerant of partial
/// failures — a rejected file shows its reason and the rest still upload.
function UploadStatusList({ items, onClear }: { items: UploadItem[]; onClear: () => void }) {
  const { t } = useTranslation("workspace");
  if (items.length === 0) return null;
  const settled = items.every((i) => i.status === "done" || i.status === "failed");
  const tag: Record<UploadItem["status"], string> = {
    queued: "text-gray-400",
    uploading: "text-amber-600 dark:text-amber-400",
    done: "text-green-600 dark:text-green-400",
    failed: "text-red-600 dark:text-red-400",
  };
  const label: Record<UploadItem["status"], string> = {
    queued: t("uploadQueued"),
    uploading: t("uploadUploading"),
    done: t("uploadDone"),
    failed: t("uploadFailed"),
  };
  return (
    <div className="border-b px-3 py-2 dark:border-gray-800">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400">{t("uploadsHeader")}</span>
        {settled && (
          <button type="button" onClick={onClear} className="text-xs text-gray-400 hover:underline">
            {t("clear")}
          </button>
        )}
      </div>
      <ul className="space-y-0.5">
        {items.map((i) => (
          <li key={i.id} className="flex items-center justify-between gap-2 text-xs">
            <span className="truncate dark:text-gray-300" title={i.name}>{i.name}</span>
            <span className={`shrink-0 ${tag[i.status]}`} title={i.error}>{label[i.status]}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/// A clickable group header (chevron + name + count) that collapses/expands the group.
/// `withBg` makes it a full-width bar (used for the headingless Ungrouped group); inside a
/// SectionHeading the surrounding row already provides the background.
function GroupHeadingButton({
  name,
  count,
  collapsed,
  onToggle,
  withBg = false,
  leading,
}: {
  name: string;
  count: number;
  collapsed: boolean;
  onToggle: () => void;
  withBg?: boolean;
  /// Optional control rendered left of the chevron (the group select-all checkbox in Select mode). Kept
  /// outside the toggle button so it isn't a nested interactive element.
  leading?: React.ReactNode;
}) {
  return (
    <div
      className={`flex min-w-0 items-center gap-1 ${
        withBg ? "w-full bg-indigo-50 px-3 py-1 dark:bg-indigo-950/40" : "flex-1"
      }`}
    >
      {leading}
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={!collapsed}
        className="flex min-w-0 flex-1 items-center gap-1 text-left"
      >
        <span aria-hidden className="text-[10px] text-indigo-400">{collapsed ? "▸" : "▾"}</span>
        <h3 className="truncate text-xs font-bold uppercase tracking-wide text-indigo-700 dark:text-indigo-300">
          {name}
        </h3>
        <span className="text-[10px] font-normal text-indigo-400 dark:text-indigo-500">({count})</span>
      </button>
    </div>
  );
}

/// The group-level select-all checkbox shown in Select mode: checked when every recording in the group is
/// selected, indeterminate when only some are. Toggling selects/deselects the whole group at once.
function GroupSelectCheckbox({
  groupName,
  ids,
  selectedIds,
  onChange,
}: {
  groupName: string;
  ids: string[];
  selectedIds: string[];
  onChange: (selectAll: boolean) => void;
}) {
  const { t } = useTranslation("workspace");
  const ref = useRef<HTMLInputElement>(null);
  const selected = ids.filter((id) => selectedIds.includes(id)).length;
  const all = ids.length > 0 && selected === ids.length;
  const some = selected > 0 && !all;
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = some;
  }, [some]);

  return (
    <input
      ref={ref}
      type="checkbox"
      checked={all}
      aria-label={t("selectAllIn", { section: groupName })}
      onChange={() => onChange(!all)}
      className="shrink-0"
    />
  );
}

function SectionHeading({
  id,
  name,
  count,
  collapsed,
  nested,
  onToggle,
  leading,
  onSectionDropBefore,
  onSectionDropNest,
}: {
  id: string;
  name: string;
  count: number;
  collapsed: boolean;
  /// A sub-section header: indented under its parent (rows below it still span the full panel width).
  nested: boolean;
  onToggle: () => void;
  leading?: React.ReactNode;
  /// A section header was dropped onto this sub-section — reorder it before this one (within its parent).
  onSectionDropBefore: (draggedSectionId: string) => void;
  /// A section header was dropped onto this top-level section — nest it under here as a sub-section.
  onSectionDropNest: (draggedSectionId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
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
    // Only top-level sections can hold sub-sections (the hierarchy is two levels deep).
    ...(nested
      ? []
      : [
          {
            label: t("newSubSection"),
            onClick: async () => {
              const sub = window.prompt(t("newSubSectionPlaceholder", { parent: name }))?.trim();
              if (!sub) return;
              await api.createSection(sub, id);
              refresh();
            },
          },
        ]),
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
    // The whole header row is the drag source (no separate handle) — it highlights on hover, mirroring the
    // recording rows. Dragging is disabled while renaming so text can be selected in the input.
    <div
      className={`flex items-center justify-between bg-indigo-50 py-1 pr-3 hover:bg-indigo-100 dark:bg-indigo-950/40 dark:hover:bg-indigo-900/60 ${
        nested ? "pl-7" : "pl-3"
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
          // Dropping a section onto a top-level header nests it; onto a sub-section header reorders it.
          if (nested) onSectionDropBefore(draggedSection);
          else onSectionDropNest(draggedSection);
        }
      }}
    >
      {/* Select-all checkbox first (matching the recording rows). */}
      {leading}
      {renaming ? (
        <SectionRenameForm initial={name} onSave={save} onCancel={() => setRenaming(false)} />
      ) : (
        <GroupHeadingButton name={name} count={count} collapsed={collapsed} onToggle={onToggle} />
      )}
      <KebabMenu actions={actions} label={t("sectionActions")} />
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
      <button type="submit" className="rounded border px-2 py-0.5 text-xs hover:bg-white/50 dark:border-gray-700 dark:hover:bg-gray-800">
        {t("common:save")}
      </button>
    </form>
  );
}

function RecordingRow({
  r,
  indentClass,
  selectMode,
  selected,
  onToggleSelect,
  onDropBefore,
}: {
  r: RecordingSummary;
  /// Left-padding class that indents the row under its section heading (e.g. "pl-6" / "pl-10").
  indentClass: string;
  selectMode: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onDropBefore: (draggedId: string) => void;
}) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => qc.invalidateQueries({ queryKey: ["recordings"] });
  const run = (fn: () => Promise<unknown>) => async () => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  };

  async function saveName(name: string) {
    await api.renameRecording(r.id, name.trim() || null);
    setRenaming(false);
    refresh();
  }

  const actions = recordingMenu({
    onRename: () => setRenaming(true),
    onCopyLink: run(() => copyRichLink(transcriptUrl(r.id), r.name ?? r.title)),
    onRetranscribe: run(async () => { await api.retranscribe(r.id); refresh(); }),
    onSummarise: run(async () => { await api.summarize(r.id); refresh(); }),
    onExtractActions: run(async () => {
      if (r.hasActions && !window.confirm(t("workspace:confirmReextract"))) return;
      await api.extractActions(r.id);
      refresh();
    }),
    onReidentify: run(async () => { await api.reidentify(r.id); refresh(); }),
    onMove: () => setMoving(true),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: run(() => api.emailTranscript(r.id)),
    onDownloadAudio: run(() => api.downloadAudio(r.id)),
    onDeleteAudio: run(async () => {
      if (!window.confirm(t("workspace:confirmDeleteAudio", { name: r.name ?? r.title }))) return;
      await api.deleteAudio(r.id);
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] }); // freed quota → refresh the account menu
    }),
    onDelete: run(async () => {
      if (!window.confirm(t("workspace:confirmDelete", { name: r.name ?? r.title }))) return;
      await api.deleteRecording(r.id);
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    }),
    hasTranscript: hasTranscript(r.status),
    hasAudio: r.hasAudio,
    isSummarizing: r.status === "Summarizing",
  }, t);

  return (
    // The whole row is the drag source (no separate handle) — it already highlights on hover. Dragging is
    // disabled while renaming so text can be selected in the input. The inner NavLink keeps draggable=false
    // so grabbing the name still drags the row, not the link.
    <li
      className={`py-2 pr-3 ${indentClass}`}
      draggable={!renaming}
      onDragStart={(e) => {
        e.dataTransfer.setData("text/plain", r.id);
        e.dataTransfer.effectAllowed = "move";
      }}
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        if (e.dataTransfer.files?.length) return; // a file upload — let it bubble to the panel drop zone
        e.preventDefault();
        e.stopPropagation(); // don't also trigger the group's append-drop
        onDropBefore(e.dataTransfer.getData("text/plain"));
      }}
    >
      <div className="flex items-center justify-between gap-1">
        {selectMode && (
          <input
            type="checkbox"
            checked={selected}
            onChange={onToggleSelect}
            aria-label={t("workspace:selectRecordingAria", { name: r.name ?? r.title })}
            className="shrink-0"
          />
        )}
        {/* Audio presence: green when the audio is available, grey once it's been deleted. */}
        <MicIcon
          on={r.hasAudio}
          title={r.hasAudio ? t("workspace:hasAudioTitle") : t("workspace:audioDeletedTitle")}
        />
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <NavLink
            to={`/recordings/${r.id}`}
            draggable={false}
            className={({ isActive }) =>
              `min-w-0 flex-1 rounded px-1 py-0.5 ${
                isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
              }`
            }
          >
            <div className="truncate text-sm font-medium dark:text-gray-100">{r.name ?? r.title}</div>
            {/* Source · date on the left; duration right-aligned (tabular-nums) so durations line up down the list. */}
            <div className="flex items-baseline justify-between gap-2 text-xs text-gray-500 dark:text-gray-400">
              <span className="truncate">
                {sourceLabel(r.source, t)} · {new Date(r.createdAt).toLocaleDateString(i18n.language)}
              </span>
              <span className="shrink-0 tabular-nums">{formatDuration(r.durationMs)}</span>
            </div>
          </NavLink>
        )}
        {showStatusBadge(r.status) && (
          <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${statusColor[r.status]}`}>
            {r.status}
          </span>
        )}
        <KebabMenu actions={actions} />
      </div>
      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
      {moving && (
        <MoveToSectionModal recordingId={r.id} currentSectionId={r.sectionId} onClose={() => setMoving(false)} />
      )}
      {downloading && <DownloadTranscriptModal recordingId={r.id} onClose={() => setDownloading(false)} />}
    </li>
  );
}

function RenameForm({
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
        placeholder={t("recordingNamePlaceholder")}
        aria-label={t("recordingNamePlaceholder")}
        className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 dark:text-gray-200">
        {t("common:save")}
      </button>
    </form>
  );
}
