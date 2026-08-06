import { useEffect, useMemo, useRef, useState } from "react";
import { NavLink, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { usePanelTab, setPanelTab, type PanelTab } from "../lib/panelTab";
import { createHub } from "../lib/signalr";
import KebabMenu from "./KebabMenu";
import ToolbarButton, { iconProps } from "./ToolbarButton";
import MoveToSectionModal from "./MoveToSectionModal";
import DownloadTranscriptModal from "./DownloadTranscriptModal";
import { recordingMenu } from "./recordingMenu";
import { isProcessing, statusLabel } from "../lib/recordingStatus";
import { copyRichLink, transcriptUrl } from "../lib/clipboard";
import { useSelection } from "../lib/selection";
import { useMoveClipboard } from "../lib/moveClipboard";
import { useRoom, useRoomBasePath, useSharedRoomId } from "../lib/rooms";
import { useActiveRecordingId } from "../lib/activeRoute";
import { formatDuration } from "../lib/format";
import { computeReorder } from "../lib/reorder";
import { useDragAutoScroll } from "../lib/dragAutoScroll";
import { buildRecordingTree, reorderBeforeSection, appendSectionUnder, type SectionNode } from "../lib/recordingTree";
import { childrenOf, breadcrumbOf, recordingCountOf, sectionCreateTarget, depthOf, MAX_FOLDER_DEPTH } from "../lib/drillView";
import { useDrillSectionId, useDrillSearch } from "../lib/drillRoute";
import { SECTION_MIME } from "../lib/dragTypes";
import DrillBreadcrumb from "./nav/DrillBreadcrumb";
import ClipboardBar from "./nav/ClipboardBar";
import SectionRow from "./nav/SectionRow";
import SearchBar from "./nav/SearchBar";
import MonthCalendar from "./MonthCalendar";
import { recordingDayKeys, dayKey, eventDayKeys, visibleGridRange, dayItems } from "../lib/calendar";
import { useUpload } from "../lib/uploadContext";
import { distinctActors, filterActions } from "../lib/actionsView";
import { recordingsForTags, topTagsByCount } from "../lib/tagCloud";
import ActionsToolbar from "./ActionsToolbar";
import ActionsTab from "./ActionsTab";
import EditActionModal from "./EditActionModal";
import TagCloud from "./TagCloud";
import TagCloudModal from "./TagCloudModal";
import type { UploadItem } from "../lib/uploadQueue";
import type { ActionListItem, CalendarEvent, RecordingStatus, RecordingSource, RecordingSummary, SectionDto } from "../lib/types";
import { RoomPermission } from "../lib/types";

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

const TAG_LIMIT_KEY = "diariz.recordings.tagLimit";

function sourceLabel(s: RecordingSource, t: TFunction): string {
  if (s === "System") return t("workspace:sourceSystem");
  if (s === "Combined") return t("workspace:sourceCombined");
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
  const { t, i18n } = useTranslation("workspace");
  const qc = useQueryClient();
  // The room being browsed (the switcher's current room). The recordings + folders lists are scoped to it;
  // the personal room keeps its folders/drag-drop, a shared room shows the recordings shared into it.
  const { currentRoom, can } = useRoom();
  const roomId = currentRoom?.id;
  const isPersonalRoom = currentRoom?.isPersonal ?? true;
  // Folders are per-room now: show/allow them when the caller can manage this room's contents (the personal
  // room's owner always can). aggRoomId is the room to scope section writes to (undefined = personal default).
  const canManageContents = can(RoomPermission.ManageContents);
  const { data: recordings = [], isLoading } = useQuery({
    queryKey: ["recordings", roomId],
    queryFn: () => api.listRecordings(roomId),
  });
  const { data: sections = [] } = useQuery({
    queryKey: ["sections", roomId],
    queryFn: () => api.listSections(roomId),
  });

  useEffect(() => {
    const hub = createHub(() => {
      qc.invalidateQueries({ queryKey: ["recordings"] });
      // Tag extraction pings the same status event when it lands, so the cloud stays live too.
      qc.invalidateQueries({ queryKey: ["tags"] });
    });
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  const tree = useMemo(() => buildRecordingTree(recordings, sections), [recordings, sections]);
  const selection = useSelection();
  // The move clipboard: cut items grey out here rather than disappear (nothing has moved yet), and pasting
  // reads the current drill level as the destination.
  const { cut, clear: clearClipboard } = useMoveClipboard();
  const basePath = useRoomBasePath();
  // Where the drill-in list is: `?in=<sectionId>`, or the room's top level. Held in the URL so browser
  // back pops a level and the position survives a reload — see `useDrillSectionId`.
  const drill = useDrillSectionId();
  // What to call the level you're in, for its "directly in ..." label. The room's name stands in at the
  // top level, since the loose recordings there belong to the room rather than to any folder.
  const currentLevelName =
    breadcrumbOf(sections, drill.sectionId).slice(-1)[0]?.name ?? currentRoom?.name ?? "";
  // Selection is global (shared with the chat panel), but the drill-in list shows only one level at a
  // time - drilling doesn't unmount the rows a prior selection was made against, it just stops rendering
  // them. Left alone, a selection made at one level survives into another where the ticked ids do not
  // even appear, so a later Cut would record the new drill level as the source for recordings that live
  // somewhere else entirely (see pasteTarget.ts's same-folder / cross-room checks, which then reason from
  // the wrong source). `selectTab` already clears the selection for the same reason on a tab switch; this
  // is the drill-position equivalent.
  useEffect(() => {
    selection.clear();
    // Reacts only to the drill position changing, not to `selection` itself - `clear` is called for
    // whatever the current render's selection is, so it does not need to be a dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [drill.sectionId]);
  // A live search takes the list body over. It is only ever component state: the drill stays in the URL, so
  // clearing the query drops straight back to where the user was browsing, with nothing to restore.
  const [searchQuery, setSearchQuery] = useState("");
  const searching = searchQuery.length > 0;
  const [opError, setOpError] = useState<string | null>(null);

  // List vs Calendar tab (persisted). The calendar shows the month, focused on today, and lists the
  // selected day's recordings below it. Held in a shared store rather than local state because the tab
  // strip is no longer the only thing that moves it: a folder chip on a recording's detail page pulls the
  // panel back to the list, and that page is a sibling of this one - see lib/panelTab.
  const tab = usePanelTab();
  const selectTab = setPanelTab;
  // Selection is per-domain (recordings vs actions) - never carry it across a tab switch. An effect, not a
  // line in selectTab, because the tab can now change from outside this component and an actions selection
  // surviving into the list would offer recording operations against action ids. Comparing against a ref
  // means this cannot fire on mount and wipe a selection the chat panel is holding.
  const prevTab = useRef(tab);
  useEffect(() => {
    if (prevTab.current === tab) return;
    prevTab.current = tab;
    selection.clear();
    selection.setSelectMode(false);
  }, [tab, selection]);
  // Actions + Tags are scoped to the room being viewed: the personal library (roomId omitted) or a shared
  // room's shared recordings. `aggRoomId` is undefined for the personal room so those endpoints keep their
  // owner-scoped path.
  const aggRoomId = isPersonalRoom ? undefined : roomId;

  // Actions tab: all actions in the current room, filtered by person + hide-complete, with one open editor.
  const { data: allActions = [] } = useQuery({
    queryKey: ["actions", "all", aggRoomId ?? null],
    queryFn: () => api.listAllActions(aggRoomId),
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
  // Tags tab: the aggregated cloud + a single selected tag filtering the recordings list below it. The
  // selection is shared with the expanded modal so the panel always mirrors what was picked there.
  const { data: tags = [] } = useQuery({
    queryKey: ["tags", aggRoomId ?? null],
    queryFn: () => api.listTags(aggRoomId),
    enabled: tab === "tags",
  });
  const [selectedTag, setSelectedTag] = useState<string | null>(null);
  const [tagCloudExpanded, setTagCloudExpanded] = useState(false);
  // Count slider: how many tags to show (the most-used first). Persisted; clamped to what's available.
  const [tagLimit, setTagLimit] = useState<number>(() => Number(localStorage.getItem(TAG_LIMIT_KEY)) || 40);
  function setTagLimitPersisted(n: number) {
    localStorage.setItem(TAG_LIMIT_KEY, String(n));
    setTagLimit(n);
  }
  // A refetch can drop the selected tag (recording deleted / re-tagged) — clear a stale selection so the
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

  const [month, setMonth] = useState(() => {
    const now = new Date();
    return { year: now.getFullYear(), month: now.getMonth() };
  });
  const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
  const dayKeys = useMemo(() => recordingDayKeys(recordings), [recordings]);

  // Google Calendar overlay: fetch the visible month's events (only when the user has connected Calendar).
  // Keyed by month, so navigating months auto-refetches; a short staleTime avoids refetch churn on focus.
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const calendarConnected = profile?.googleCalendar === true;
  const { data: calendarEvents = [], isFetching: eventsFetching } = useQuery({
    queryKey: ["calendar-events", month.year, month.month],
    queryFn: () => {
      const { timeMin, timeMax } = visibleGridRange(month.year, month.month);
      return api.getCalendarEvents(timeMin, timeMax);
    },
    // A shared room shows only its recordings on the calendar - no personal Google-event overlay.
    enabled: calendarConnected && isPersonalRoom,
    staleTime: 5 * 60_000,
    retry: false,
  });
  // The Google overlay is personal-only. Force events empty in a shared room (the disabled query still holds
  // the last personal-room data in cache - the key is room-agnostic - so gate the derived value, not just the fetch).
  const showCalendarOverlay = calendarConnected && isPersonalRoom;
  const events = isPersonalRoom ? calendarEvents : [];
  const eventKeys = useMemo(() => eventDayKeys(events), [events]);
  const selectedItems = selectedDay ? dayItems(recordings, events, selectedDay) : [];
  function stepMonth(delta: number) {
    setMonth((m) => {
      const d = new Date(m.year, m.month + delta, 1);
      return { year: d.getFullYear(), month: d.getMonth() };
    });
  }
  /// Apply a drag-and-drop: set the dragged recording's group + order within the current room, then refresh.
  /// Order and folders are per-room, so this needs manage-contents (the personal-room owner always has it).
  async function drop(sectionId: string | null, groupIds: string[], draggedId: string, beforeId: string | null) {
    if (!draggedId || !canManageContents) return;
    await api.reorderRecordings(sectionId, computeReorder(groupIds, draggedId, beforeId), aggRoomId);
    qc.invalidateQueries({ queryKey: ["recordings"] });
  }

  /// Section drag-and-drop. The server may reject a move whose target is the section itself or one of
  /// its own descendants, or whose branch would not fit within the depth cap - surface that in the banner.
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
    if (payload) runSection(() => api.reorderSections(payload.parentId, payload.orderedIds, aggRoomId));
  }
  /// Drop a section into a top-level section's body (nest it) or onto the Ungrouped bar (promote to top).
  function nestSection(parentId: string | null, draggedId: string) {
    if (!draggedId || draggedId === parentId) return;
    const payload = appendSectionUnder(sections, draggedId, parentId);
    runSection(() => api.reorderSections(payload.parentId, payload.orderedIds, aggRoomId));
  }

  /// Perform the move clipboard's pending paste into the current drill level. Recordings go through the
  /// bulk move endpoint in one call; a folder goes through the same reorderSections call the drag-and-drop
  /// "nest" gesture already uses (appendSectionUnder), so both land at the bottom of the target, after
  /// whatever is already there, preserving its existing relative order. A failed paste leaves the clipboard
  /// intact and surfaces the error the same way the other section operations do - losing the cut to a
  /// network blip would be worse than the error itself.
  async function pasteClipboard() {
    if (!cut) return;
    setOpError(null);
    try {
      if (cut.kind === "recordings") {
        await api.moveRecordingsBulk(cut.ids, drill.sectionId, aggRoomId);
      } else {
        // cut.ids[0]: cutFolder always stores exactly one id - folders are cut one at a time, there is no
        // folder multi-select (see moveClipboard.tsx). pasteTarget still loops over cut.ids for the
        // "folders" kind, so the two sides would disagree the moment a future multi-select folder cut
        // exists; if that ever changes, this line needs to become a loop too.
        const payload = appendSectionUnder(sections, cut.ids[0], drill.sectionId);
        await api.reorderSections(payload.parentId, payload.orderedIds, aggRoomId);
      }
      clearClipboard();
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["sections"] });
    } catch (e) {
      setOpError(apiErrorMessage(e));
    }
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

  // Which ids are the clipboard's current cut, by kind — greyed out in the rows below rather than removed
  // (nothing has moved until the paste succeeds).
  const cutRecordingIds = cut?.kind === "recordings" ? cut.ids : [];
  const cutFolderIds = cut?.kind === "folders" ? cut.ids : [];

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
            cut={cutRecordingIds.includes(r.id)}
          />
        ))}
      </ul>
    );
  };

  // The one level the drill-in list is showing: the folders inside `drill.sectionId` plus the recordings
  // filed directly in it. At the root that is the top-level folders plus the ungrouped recordings — which
  // is why "Ungrouped" is no longer a special case, it is just the root's own items.
  const level = childrenOf(tree, drill.sectionId);
  const levelIds = level.items.map((i) => i.id);
  // A folder row on this level may take sub-folders as long as one more level still fits. The rows are one
  // level below the drill position, so their own depth is the drill's depth + 1.
  const childrenCanNest = depthOf(sections, drill.sectionId) + 1 < MAX_FOLDER_DEPTH;

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
        <ListToolbar
          recordings={recordings}
          listMode={tab === "list"}
          allowFolders={canManageContents}
          sections={sections}
          drillSectionId={drill.sectionId}
          roomId={aggRoomId}
          onError={setOpError}
        />
      )}
      <div className="flex min-h-0 flex-1">
        <TabStrip tab={tab} onSelect={selectTab} />
        {tab === "list" ? (
          // min-w-0 lets this flex child shrink to the panel width so long recording names truncate
          // instead of forcing the column wider than the panel.
          // min-w-0 lets this flex child shrink to the panel width so long recording names truncate
          // instead of forcing the column wider than the panel.
          <div className="flex min-h-0 min-w-0 flex-1 flex-col">
            {/* Pinned: search is always reachable, whatever level you're on. */}
            <SearchBar
              roomId={roomId}
              sectionId={drill.sectionId}
              scopeName={currentLevelName}
              onQueryChange={setSearchQuery}
              onDrill={(id) => drill.drillTo(id)}
            />
            {/* The breadcrumb stays put during a search: the drill is not disturbed by typing, and the user
                can see where they will land the moment they clear the query. */}
            <DrillBreadcrumb
              sections={sections}
              sectionId={drill.sectionId}
              basePath={basePath}
              onDrill={drill.drillTo}
              onRecordingDrop={(sectionId, recordingId) =>
                // Append after what's already there, same as dropping onto a folder row - an empty id list
                // here would land the recording at position 0 (the top), giving one gesture two behaviours.
                drop(sectionId, childrenOf(tree, sectionId).items.map((i) => i.id), recordingId, null)
              }
            />
            {/* Persistent, like the breadcrumb: the clipboard survives navigation, so this stays visible and
                shows where a paste would land even while the user is searching. */}
            <ClipboardBar
              sections={sections}
              destSectionId={drill.sectionId}
              destRoomId={aggRoomId ?? null}
              onPaste={pasteClipboard}
            />
            {/* Hoisted above the search guard, same as the bar above it: a paste (from this bar) can fail
                while the list body is showing search results, and the error must be exactly as reachable
                as the control that produced it - a click that silently does nothing is worse than an
                error the user has to scroll past. */}
            {opError && <p className="px-3 py-1 text-xs text-red-600 dark:text-red-400">{opError}</p>}
            {/* The results replace the list body outright rather than hiding it: nothing below is reachable
                or readable during a search, and clearing rebuilds it from the URL anyway. */}
            {!searching && (
            <div
              ref={listScrollRef}
              className="min-h-0 min-w-0 flex-1 overflow-y-auto"
              // Dropping onto the level's background files the dragged item here: a recording moves into
              // this folder (or out to Ungrouped at the root), a folder is reparented to this level.
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => {
                const draggedSection = e.dataTransfer.getData(SECTION_MIME);
                if (draggedSection) {
                  // The level's background reparents to the level itself - at the root that is a promotion to
                  // top level, which is always legal regardless of how deep this level's children may go.
                  nestSection(drill.sectionId, draggedSection);
                  return;
                }
                const dragged = e.dataTransfer.getData("text/plain");
                if (dragged) drop(drill.sectionId, levelIds, dragged, null);
              }}
            >
              <UploadStatusList items={upload.items} onClear={upload.clearFinished} />
              {dragging && (
                <p className="px-3 py-2 text-center text-xs font-medium text-blue-600 dark:text-blue-400">
                  {t("dropToUpload")}
                </p>
              )}
              {recordings.length === 0 && !dragging && (
                <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("noRecordings")}</p>
              )}

              {level.sections.map((node) => (
                <SectionRow
                  key={node.id}
                  id={node.id}
                  name={node.name}
                  count={recordingCountOf(tree, node.id)}
                  canNest={childrenCanNest}
                  parentSectionId={drill.sectionId}
                  cut={cutFolderIds.includes(node.id)}
                  onDrill={() => drill.drillTo(node.id)}
                  onSectionDropBefore={(draggedId) => dropSectionBefore(node.id, draggedId)}
                  onSectionDropNest={(draggedId) => nestSection(node.id, draggedId)}
                  onRecordingDrop={(draggedId) => drop(node.id, node.items.map((i) => i.id), draggedId, null)}
                />
              ))}

              {level.items.length > 0 && (
                <div className="flex items-center gap-2 px-2 pb-1 pt-2">
                  {selectAllFor({ id: drill.sectionId ?? "__root__", name: currentLevelName, items: level.items, children: [] })}
                  <p className="truncate text-[10px] font-semibold uppercase tracking-wide text-gray-400 dark:text-gray-500">
                    {t("drillDirectlyIn", { name: currentLevelName })} · {level.items.length}
                  </p>
                </div>
              )}
              {rowList(drill.sectionId, level.items)}

              {/* A folder you drilled into that holds nothing at all — distinct from a library with no
                  recordings yet, which the noRecordings message above covers. */}
              {recordings.length > 0 && level.sections.length === 0 && level.items.length === 0 && (
                <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("drillEmpty")}</p>
              )}
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
                daysWithEvents={showCalendarOverlay ? eventKeys : undefined}
                selectedKey={selectedDay}
                onSelect={setSelectedDay}
                onPrev={() => stepMonth(-1)}
                onNext={() => stepMonth(1)}
              />
              {showCalendarOverlay && (
                <div className="flex items-center justify-end px-2 pb-1">
                  <button
                    type="button"
                    onClick={() => qc.invalidateQueries({ queryKey: ["calendar-events", month.year, month.month] })}
                    disabled={eventsFetching}
                    className="text-[10px] text-gray-400 hover:text-gray-600 disabled:opacity-50 dark:text-gray-500 dark:hover:text-gray-300"
                  >
                    {eventsFetching ? t("calRefreshing") : t("calRefreshEvents")}
                  </button>
                </div>
              )}
            </div>
            {/* Reserve the scrollbar gutter so toggling the day list's scrollbar never shifts its width. */}
            <div ref={dayScrollRef} className="min-h-0 min-w-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
              {selectedItems.length === 0 ? (
                <p className="p-4 text-sm text-gray-500 dark:text-gray-400">
                  {showCalendarOverlay ? t("calDayEmpty") : t("calNoRecordings")}
                </p>
              ) : (
                <ul className="divide-y dark:divide-gray-800">
                  {selectedItems.map((it) =>
                    it.type === "recording" ? (
                      <RecordingRow
                        key={it.recording.id}
                        r={it.recording}
                        indentClass="pl-3"
                        selectMode={selection.selectMode}
                        selected={selection.selectedIds.includes(it.recording.id)}
                        onToggleSelect={() => selection.toggle(it.recording.id)}
                        onDropBefore={() => {}}
                      />
                    ) : (
                      <EventRow key={`ev-${it.event.id}`} event={it.event} locale={i18n.language} t={t} />
                    ),
                  )}
                </ul>
              )}
            </div>
          </div>
        ) : tab === "actions" ? (
          // Actions: a flat, cross-transcript list with its own filter/select/complete toolbar above.
          <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">
            {opError && <p className="px-3 py-1 text-xs text-red-600 dark:text-red-400">{opError}</p>}
            <ActionsTab actions={visibleActions} persons={persons} person={personFilter} onPerson={setPersonFilter} />
          </div>
        ) : (
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
          </div>
        )}
      </div>
      {editingAction && <EditActionModal action={editingAction} onClose={() => setEditingAction(null)} />}
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

/// A slider that limits how many tags the cloud shows (the most-used first). Exported so the expanded modal
/// reuses the exact control. Hidden when there are 2 or fewer tags (nothing to trim).
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

/// Vertical List / Calendar / Actions tabs, sitting to the left of the panel's scroll area (below the toolbar).
function TabStrip({
  tab,
  onSelect,
}: {
  tab: PanelTab;
  onSelect: (t: PanelTab) => void;
}) {
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
      {item("tags", t("tabTags"))}
    </div>
  );
}

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
    const displayIndex = new Map(recordings.map((r, i) => [r.id, i]));
    const ordered = [...selectedIds].sort((a, b) => (displayIndex.get(a) ?? 0) - (displayIndex.get(b) ?? 0));
    cutRecordings(ordered, drillSectionId, roomId ?? null);
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

/// A small calendar glyph marking that a recording is linked to a Google Calendar event. Tinted the linked
/// calendar's Google colour when known (else green); absent when the recording isn't linked to a meeting.
function CalendarIcon({ title, color }: { title: string; color?: string | null }) {
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
      style={color ? { color } : undefined}
      className={`shrink-0 ${color ? "" : "text-green-600 dark:text-green-400"}`}
    >
      <title>{title}</title>
      <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
      <line x1="16" y1="2" x2="16" y2="6" />
      <line x1="8" y1="2" x2="8" y2="6" />
      <line x1="3" y1="10" x2="21" y2="10" />
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

/// A Google Calendar event row in the Calendar tab's merged day list — time range + title. Clicking the row
/// opens the event preview (a meeting with no recording); the calendar glyph still links out to Google.
/// Only unlinked events reach this row (a linked event is shown by its recording row, deduped in `dayItems`).
/// Events from an external .ics feed (`calendarId` starting `ics:`) are display-only - they have no Google
/// event to preview or link a recording to - so their row is a static (non-clickable) block, still coloured.
function EventRow({ event, locale, t }: { event: CalendarEvent; locale: string; t: TFunction }) {
  const fmt = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" });
  const title = event.summary || t("calUntitledEvent");
  const range = `${fmt.format(new Date(event.start))} – ${fmt.format(new Date(event.end))}`;
  const isFeed = event.calendarId?.startsWith("ics:") ?? false;

  const inner = (
    <>
      <svg
        {...iconProps}
        style={event.color ? { color: event.color } : undefined}
        className={`mt-0.5 h-3.5 w-3.5 shrink-0 ${event.color ? "" : "text-green-600 dark:text-green-400"}`}
        aria-label={t("calEventLabel")}
      >
        <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
        <line x1="16" y1="2" x2="16" y2="6" />
        <line x1="8" y1="2" x2="8" y2="6" />
        <line x1="3" y1="10" x2="21" y2="10" />
      </svg>
      <div className="min-w-0 flex-1">
        <div className="truncate text-gray-800 dark:text-gray-200">{title}</div>
        <div className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
          <span className="tabular-nums">{range}</span>
          {event.calendarName && <span className="truncate">· {event.calendarName}</span>}
        </div>
      </div>
    </>
  );

  return (
    <li>
      {isFeed ? (
        <div className="flex items-start gap-2 py-1.5 pl-3 pr-2 text-sm">{inner}</div>
      ) : (
        <NavLink
          to={`/calendar-event/${encodeURIComponent(event.id)}`}
          className={({ isActive }) =>
            `flex items-start gap-2 py-1.5 pl-3 pr-2 text-sm ${
              isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
            }`
          }
        >
          {inner}
        </NavLink>
      )}
    </li>
  );
}

export function RecordingRow({
  r,
  indentClass,
  selectMode,
  selected,
  onToggleSelect,
  onDropBefore,
  showDate = false,
  onNavigate,
  cut = false,
}: {
  r: RecordingSummary;
  /// Left-padding class that indents the row under its section heading (e.g. "pl-6" / "pl-10").
  indentClass: string;
  selectMode: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onDropBefore: (draggedId: string) => void;
  /// Show a second line with the source + date/time (the dense list keeps these in the hover title; the
  /// Tags views have room to show them). Off by default so the list/calendar rows are unchanged.
  showDate?: boolean;
  /// Called when the row's name link is clicked - lets the expanded modal close itself as it navigates.
  onNavigate?: () => void;
  /// This recording is the move clipboard's current cut - greyed out with a dashed outline rather than
  /// hidden, since nothing has actually moved yet (see RecordingsPanel's paste flow).
  cut?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const basePath = useRoomBasePath();
  const sharedRoomId = useSharedRoomId();
  const activeRecordingId = useActiveRecordingId();
  // The row links back into the drill level it was rendered from. Empty outside the List tab (the
  // Calendar/Tags lists aren't drilled), so those links are unchanged.
  const drillSearch = useDrillSearch();
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
      // If the deleted recording is the one open in the detail panel, leave it — otherwise its transcript
      // stays on screen and any further action targets a now-missing recording.
      if (activeRecordingId === r.id) navigate(basePath || "/");
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    }),
    hasTranscript: hasTranscript(r.status),
    hasAudio: r.hasAudio,
    isSummarizing: r.status === "Summarizing",
    isProcessing: isProcessing(r.status),
  }, t);

  return (
    // The whole row is the drag source (no separate handle) — it already highlights on hover. Dragging is
    // disabled while renaming so text can be selected in the input. The inner NavLink keeps draggable=false
    // so grabbing the name still drags the row, not the link.
    <li
      // `outline` (not `border`) for the cut indicator: this row sits inside a `divide-y` list, whose
      // divider rule targets `border-*` on every child via a compound selector that would outrank (or,
      // for the dark-mode colour, race on stylesheet order against) a plain border class here regardless
      // of class-attribute order. `outline` is a separate CSS property the divider never touches, so the
      // dashed ring is guaranteed visible rather than winning by luck.
      className={`py-0.5 pr-3 ${indentClass} ${cut ? "opacity-50 rounded outline outline-dashed outline-1 outline-gray-400 dark:outline-gray-600" : ""}`}
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
      {/* Colour/opacity alone would leave a screen-reader user unable to tell which rows are cut - the
          bar's count says something is cut, never which. */}
      {cut && <span className="sr-only">{t("workspace:cutPendingPasteAria")}</span>}
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
        {/* Calendar link: shown alongside the mic icon when the recording is linked to a meeting, tinted the
            linked calendar's Google colour. */}
        {r.calendarEventId && <CalendarIcon title={t("workspace:hasCalendarTitle")} color={r.calendarColor} />}
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <NavLink
            // Keeps `?in=` so opening a recording doesn't pop the list back to the root behind it.
            to={{ pathname: `${basePath}/recordings/${r.id}`, search: drillSearch }}
            draggable={false}
            onClick={() => onNavigate?.()}
            // Single-line row: name + right-aligned duration. Source + date (and the full, untruncated name)
            // move to the hover tooltip to keep the list dense (unless showDate puts the date on a 2nd line).
            title={`${r.name ?? r.title} — ${sourceLabel(r.source, t)} · ${new Date(r.createdAt).toLocaleDateString(i18n.language)}`}
            className={({ isActive }) =>
              `flex min-w-0 flex-1 gap-2 rounded px-1 py-0.5 leading-tight ${showDate ? "items-start" : "items-baseline"} ${
                isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
              }`
            }
          >
            <span className="min-w-0 flex-1">
              <span className="block truncate text-sm font-medium dark:text-gray-100">{r.name ?? r.title}</span>
              {showDate && (
                <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
                  {sourceLabel(r.source, t)} · {new Date(r.createdAt).toLocaleString(i18n.language, { dateStyle: "medium", timeStyle: "short" })}
                </span>
              )}
            </span>
            {/* Duration right-aligned (tabular-nums) so durations line up down the list. */}
            <span className="shrink-0 tabular-nums text-xs text-gray-500 dark:text-gray-400">
              {formatDuration(r.durationMs)}
            </span>
          </NavLink>
        )}
        {showStatusBadge(r.status) && (
          <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${statusColor[r.status]}`}>
            {statusLabel(r.status)}
          </span>
        )}
        <KebabMenu actions={actions} />
      </div>
      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
      {moving && (
        <MoveToSectionModal recordingId={r.id} currentSectionId={r.sectionId} roomId={sharedRoomId} onClose={() => setMoving(false)} />
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
