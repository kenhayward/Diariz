/// Compute the new ordered id list for a group after a drag-and-drop.
/// `groupIds` are the group's current recording ids in order; `dragged` is the dropped recording, or the
/// **set** of them when several ticked rows move together; `beforeId` is the row to insert before, or null
/// to append at the end. Dragged ids not already in the group are inserted (they have moved in from
/// another group), and their relative order is preserved.
///
/// Sections still pass a single id - a folder is dragged on its own - so the parameter takes either.
export function computeReorder(
  groupIds: string[],
  dragged: string | string[],
  beforeId: string | null,
): string[] {
  const ids = Array.isArray(dragged) ? dragged : [dragged];
  const moving = new Set(ids);
  const without = groupIds.filter((id) => !moving.has(id));
  // Dropping onto one of the rows that is itself moving has no meaningful insertion point, so it appends -
  // the single-item "dropped on itself" rule, generalised.
  if (beforeId === null || moving.has(beforeId)) return [...without, ...ids];
  const idx = without.indexOf(beforeId);
  if (idx < 0) return [...without, ...ids];
  return [...without.slice(0, idx), ...ids, ...without.slice(idx)];
}

/// Sort ids into the order their rows are displayed in.
///
/// Selection is held in TICK order (checkbox toggles append), which is not the order anything should be
/// moved in: ticking the third row then the first would otherwise cut or drag them third-then-first. Every
/// bulk move promises to preserve *relative* order, and that can only mean the order the rows are shown in.
/// Ids not in `displayed` (a row scrolled out of the current level, say) keep their relative order at the
/// end rather than being dropped - losing a recording from a move is worse than misplacing it.
export function inDisplayOrder(ids: string[], displayed: string[]): string[] {
  const at = new Map(displayed.map((id, i) => [id, i]));
  return [...ids].sort((a, b) => (at.get(a) ?? Infinity) - (at.get(b) ?? Infinity));
}

/// The recordings a drag should carry: the whole selection when the dragged row is part of it, otherwise
/// just that row.
///
/// Dragging a row that is **not** selected deliberately moves only that row. Sweeping up ticked rows from
/// elsewhere on an unrelated drag would be a surprise, and it is the convention every file manager follows.
export function draggedRecordingIds(
  draggedId: string,
  selectedIds: string[],
  displayed: string[],
): string[] {
  if (!selectedIds.includes(draggedId)) return [draggedId];
  return inDisplayOrder(selectedIds, displayed);
}
