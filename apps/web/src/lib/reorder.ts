/// Compute the new ordered id list for a group after a drag-and-drop.
/// `groupIds` are the group's current recording ids in order; `draggedId` is the dropped recording;
/// `beforeId` is the row to insert before, or null to append at the end. A `draggedId` not already
/// in the group is inserted (it has moved in from another group).
export function computeReorder(groupIds: string[], draggedId: string, beforeId: string | null): string[] {
  const without = groupIds.filter((id) => id !== draggedId);
  if (beforeId === null || beforeId === draggedId) return [...without, draggedId];
  const idx = without.indexOf(beforeId);
  if (idx < 0) return [...without, draggedId];
  return [...without.slice(0, idx), draggedId, ...without.slice(idx)];
}
