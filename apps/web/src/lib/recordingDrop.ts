import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "./api";
import { useSelection } from "./selection";
import { useMoveClipboard } from "./moveClipboard";
import { computeReorder, draggedRecordingIds } from "./reorder";
import { reorderBeforeSection, appendSectionUnder } from "./recordingTree";
import type { RecordingSummary, SectionDto } from "./types";

/// Every write the recordings list can perform by dragging or pasting: move recordings between folders and
/// within one, reorder and reparent folders, and complete a cut/paste. Plus the error banner they all
/// report into.
///
/// This is **not** a pure module and does not pretend to be. Every genuinely pure piece was already
/// extracted and unit-tested elsewhere - `computeReorder` and `draggedRecordingIds` (lib/reorder.ts),
/// `reorderBeforeSection` and `appendSectionUnder` (lib/recordingTree.ts), `pasteTarget.ts` for whether a
/// paste is allowed at all. What is left is API calls, cache invalidation and three guards, and wrapping
/// that in a "pure builder" layer would be theatre.
///
/// It lives here for a different reason: this is the hottest code in the app - 13 of the 15 commits before
/// this refactor touched folder drill, drag-drop or the clipboard - and it was sharing a component body
/// with the tabs, the queries and the search box. Now the next change to a drop rule has one small file to
/// land in.
///
/// `useSelection`, `useMoveClipboard` and `useQueryClient` are read **inside** rather than passed in: it
/// keeps the signature to plain data, and more importantly it keeps `drop`'s read of `selectedIds` current
/// rather than closing over whatever the caller had at its last render.
export function useRecordingDrop({
  recordings,
  sections,
  sectionId,
  roomId,
  canManageContents,
}: {
  recordings: RecordingSummary[];
  sections: SectionDto[];
  /// Where the list is drilled to - the paste destination, and the folder a background drop files into.
  sectionId: string | null;
  /// The room to scope writes to (undefined = the caller's personal room).
  roomId: string | undefined;
  canManageContents: boolean;
}) {
  const qc = useQueryClient();
  const selection = useSelection();
  const { cut, clear: clearClipboard } = useMoveClipboard();
  const [opError, setOpError] = useState<string | null>(null);

  /// Apply a drag-and-drop: set the dragged recordings' group + order within the current room, then refresh.
  /// Order and folders are per-room, so this needs manage-contents (the personal-room owner always has it).
  ///
  /// Every recording drop funnels through here - onto a folder row, onto a breadcrumb crumb, onto the level
  /// background, or between two rows - which is why the multi-select rule lives here and nowhere else:
  /// dragging one of several ticked rows moves the whole set, in the order the rows are shown.
  ///
  /// Reports failures in the banner like every other list operation. Not optional politeness: all four call
  /// sites are **sync** drop handlers, so this promise is floated - without the catch a rejected reorder
  /// (a 403 after a permission change, the server's depth cap) left the row springing back to where it
  /// started with nothing said, and an unhandled rejection in the console.
  async function drop(
    targetSectionId: string | null,
    groupIds: string[],
    draggedId: string,
    beforeId: string | null,
  ) {
    if (!draggedId || !canManageContents) return;
    setOpError(null);
    try {
      const ids = draggedRecordingIds(draggedId, selection.selectedIds, recordings.map((r) => r.id));
      await api.reorderRecordings(targetSectionId, computeReorder(groupIds, ids, beforeId), roomId);
      qc.invalidateQueries({ queryKey: ["recordings"] });
    } catch (e) {
      // Deliberately not `runSection`, which invalidates ["sections"] too: no folder changed here, and a
      // second refetch of the tree on every row drag is a cost with nothing to show for it.
      setOpError(apiErrorMessage(e));
    }
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
    if (payload) runSection(() => api.reorderSections(payload.parentId, payload.orderedIds, roomId));
  }

  /// Drop a section into a top-level section's body (nest it) or onto the Ungrouped bar (promote to top).
  function nestSection(parentId: string | null, draggedId: string) {
    if (!draggedId || draggedId === parentId) return;
    const payload = appendSectionUnder(sections, draggedId, parentId);
    runSection(() => api.reorderSections(payload.parentId, payload.orderedIds, roomId));
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
        await api.moveRecordingsBulk(cut.ids, sectionId, roomId);
      } else {
        // cut.ids[0]: cutFolder always stores exactly one id - folders are cut one at a time, there is no
        // folder multi-select (see moveClipboard.tsx). pasteTarget still loops over cut.ids for the
        // "folders" kind, so the two sides would disagree the moment a future multi-select folder cut
        // exists; if that ever changes, this line needs to become a loop too.
        const payload = appendSectionUnder(sections, cut.ids[0], sectionId);
        await api.reorderSections(payload.parentId, payload.orderedIds, roomId);
      }
      clearClipboard();
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["sections"] });
    } catch (e) {
      setOpError(apiErrorMessage(e));
    }
  }

  // Deliberately NOT wrapped in useCallback. These are plain declarations re-created each render, exactly
  // as they were in the component - memoising them here would introduce stale-closure risk against
  // `recordings` and `selectedIds` for no measured gain.
  return { opError, setOpError, drop, dropSectionBefore, nestSection, pasteClipboard };
}
