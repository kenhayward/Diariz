import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../lib/api";
import { usePanelTab, setPanelTab } from "../lib/panelTab";
import { createHub } from "../lib/signalr";
import { useSelection } from "../lib/selection";
import { useMoveClipboard } from "../lib/moveClipboard";
import { useRecordingDrop } from "../lib/recordingDrop";
import { useRoom, useRoomBasePath } from "../lib/rooms";
import { useDragAutoScroll } from "../lib/dragAutoScroll";
import { buildRecordingTree } from "../lib/recordingTree";
import { childrenOf, breadcrumbOf, recordingCountOf, depthOf, MAX_FOLDER_DEPTH } from "../lib/drillView";
import { useDrillSectionId } from "../lib/drillRoute";
import { SECTION_MIME, dragHasFiles } from "../lib/dragTypes";
import DrillBreadcrumb from "./nav/DrillBreadcrumb";
import ClipboardBar from "./nav/ClipboardBar";
import SectionRow from "./nav/SectionRow";
import SearchBar from "./nav/SearchBar";
import { RecordingRow } from "./nav/RecordingRow";
import TabStrip from "./nav/TabStrip";
import ListToolbar from "./nav/ListToolbar";
import UploadStatusList from "./nav/UploadStatusList";
import GroupSelectCheckbox from "./nav/GroupSelectCheckbox";
import CalendarTab from "./nav/CalendarTab";
import TagsTab from "./nav/TagsTab";
import { useUpload } from "../lib/uploadContext";
import { distinctActors, filterActions } from "../lib/actionsView";
import ActionsToolbar from "./ActionsToolbar";
import ActionsTab from "./ActionsTab";
import EditActionModal from "./EditActionModal";
import type { ActionListItem, RecordingSummary } from "../lib/types";
import { RoomPermission } from "../lib/types";

/// The recordings list for the left panel, grouped into user sections (Ungrouped last).
/// Selecting a row routes to /recordings/:id (middle panel).
export default function RecordingsPanel() {
  const { t } = useTranslation("workspace");
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
  const { cut } = useMoveClipboard();
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
  //
  // Compared against a ref for the same reason the tab effect below is: this must not fire on **mount**.
  // Collapsing the left sidebar unmounts this panel while `SelectionProvider` and the chat panel stay up
  // (Workspace.tsx), so an unguarded clear here emptied the chat panel's "Selected transcripts" context
  // every time the sidebar was reopened - a selection wiped by a gesture that never touched the list.
  const prevDrill = useRef(drill.sectionId);
  useEffect(() => {
    if (prevDrill.current === drill.sectionId) return;
    prevDrill.current = drill.sectionId;
    selection.clear();
    // Reacts only to the drill position changing, not to `selection` itself - `clear` is called for
    // whatever the current render's selection is, so it does not need to be a dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [drill.sectionId]);
  // A live search takes the list body over. It is only ever component state: the drill stays in the URL, so
  // clearing the query drops straight back to where the user was browsing, with nothing to restore.
  const [searchQuery, setSearchQuery] = useState("");
  const searching = searchQuery.length > 0;
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

  // Every write the list can perform by dragging or pasting, plus the banner they report into. Extracted
  // because this is the code the last 15 commits kept landing in - see lib/recordingDrop.ts.
  const { opError, setOpError, drop, dropSectionBefore, nestSection, pasteClipboard } = useRecordingDrop({
    recordings,
    sections,
    sectionId: drill.sectionId,
    roomId: aggRoomId,
    canManageContents,
  });

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
  // Drag audio files anywhere onto the panel to upload them (distinct from the reorder DnD, which uses
  // the "text/plain" payload — file drags carry "Files"). A depth counter keeps the highlight stable as
  // the cursor moves over child rows.
  const upload = useUpload();
  const [dragging, setDragging] = useState(false);
  const dragDepth = useRef(0);
  // Native HTML5 DnD doesn't scroll the list while dragging near its edges, so a drop target outside the
  // viewport is unreachable in a long tree. This auto-scrolls the list during any drag.
  const listScrollRef = useDragAutoScroll<HTMLDivElement>();
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
    // Where you dropped is where they go: the level the list is showing wins over the placement preference,
    // which only decides for an upload that named no destination (the Upload button).
    upload.uploadFiles(Array.from(e.dataTransfer.files), { sectionId: drill.sectionId });
  }

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;

  // Select mode: a checkbox that selects/deselects every recording directly in this section at once.
  /// The select-all checkbox for a group of rows, or nothing when there is no group to select (not in
  /// select mode, or the level is empty). Takes the name and ids rather than a `SectionNode`: the root
  /// level is not a node, and asking for one made the call site invent `{ id: "__root__", children: [] }`.
  const selectAllFor = (groupName: string, ids: string[]) => {
    if (!selection.selectMode || ids.length === 0) return undefined;
    return (
      <GroupSelectCheckbox
        groupName={groupName}
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

  const rowList = (sectionId: string | null, items: RecordingSummary[]) => {
    const ids = items.map((i) => i.id);
    return (
      <ul className="divide-y dark:divide-gray-800">
        {items.map((r) => (
          <RecordingRow
            key={r.id}
            r={r}
            indentClass="pl-3"
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
      <TabStrip tab={tab} onSelect={selectTab} />
      <div className="flex min-h-0 flex-1">
        {tab === "list" ? (
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
                  {selectAllFor(currentLevelName, levelIds)}
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
          <CalendarTab recordings={recordings} isPersonalRoom={isPersonalRoom} />
        ) : tab === "actions" ? (
          // Actions: a flat, cross-transcript list with its own filter/select/complete toolbar above.
          <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">
            {opError && <p className="px-3 py-1 text-xs text-red-600 dark:text-red-400">{opError}</p>}
            <ActionsTab actions={visibleActions} persons={persons} person={personFilter} onPerson={setPersonFilter} />
          </div>
        ) : (
          <TagsTab recordings={recordings} roomId={aggRoomId} />
        )}
      </div>
      {editingAction && <EditActionModal action={editingAction} onClose={() => setEditingAction(null)} />}
    </div>
  );
}

