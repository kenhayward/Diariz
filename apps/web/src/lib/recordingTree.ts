import type { RecordingSummary, SectionDto } from "./types";
import { computeReorder } from "./reorder";

/// A section node in the two-level recordings tree. Top-level sections may have `children`
/// (sub-sections); sub-sections never do (the hierarchy is capped at two levels). `items` are the
/// recordings filed directly under this section — recordings may live at either level.
export interface SectionNode {
  id: string;
  name: string;
  items: RecordingSummary[];
  children: SectionNode[];
}

export interface RecordingTree {
  /// Top-level sections in display order; each may carry direct recordings and sub-sections.
  sections: SectionNode[];
  /// Recordings with no section.
  ungrouped: RecordingSummary[];
}

function bySiblingOrder(a: SectionDto, b: SectionDto): number {
  return (a.position ?? 0) - (b.position ?? 0) || a.name.localeCompare(b.name);
}

/// Build the two-level tree the recordings panel renders. Recordings are filed under their
/// `sectionId` (top-level or sub-section); unknown section ids (e.g. the sections list hasn't loaded
/// yet) fall back to a synthetic top-level section using the recording's own `sectionName`.
export function buildRecordingTree(recordings: RecordingSummary[], sections: SectionDto[]): RecordingTree {
  const known = new Map(sections.map((s) => [s.id, s]));
  const recsBySection = new Map<string, RecordingSummary[]>();
  const ungrouped: RecordingSummary[] = [];
  for (const r of recordings) {
    if (!r.sectionId) {
      ungrouped.push(r);
      continue;
    }
    const arr = recsBySection.get(r.sectionId) ?? [];
    arr.push(r);
    recsBySection.set(r.sectionId, arr);
  }

  // Treat a null/undefined parent as top-level (defensive against partially-populated section data).
  const tops = sections.filter((s) => !s.parentId).sort(bySiblingOrder);
  const subsByParent = new Map<string, SectionDto[]>();
  for (const s of sections) {
    if (s.parentId && known.has(s.parentId)) {
      const arr = subsByParent.get(s.parentId) ?? [];
      arr.push(s);
      subsByParent.set(s.parentId, arr);
    }
  }

  const node = (s: SectionDto, children: SectionNode[] = []): SectionNode => ({
    id: s.id,
    name: s.name,
    items: recsBySection.get(s.id) ?? [],
    children,
  });

  const sectionNodes = tops.map((top) =>
    node(top, (subsByParent.get(top.id) ?? []).sort(bySiblingOrder).map((sub) => node(sub))),
  );

  // Recordings pointing at a section we don't know yet → synthetic top-level groups (load-order safety).
  for (const [sectionId, items] of recsBySection) {
    if (!known.has(sectionId)) {
      sectionNodes.push({ id: sectionId, name: items[0]?.sectionName ?? "Section", items, children: [] });
    }
  }

  return { sections: sectionNodes, ungrouped };
}

/// Reorder/reparent for section drag-and-drop: place `draggedId` immediately before `targetId`,
/// adopting the target's parent. Returns the `{ parentId, orderedIds }` payload for `reorderSections`
/// (the sibling list under the target's parent, with the dragged section moved in). Returns null for a
/// no-op (dropping a section on itself).
export function reorderBeforeSection(
  sections: SectionDto[],
  draggedId: string,
  targetId: string,
): { parentId: string | null; orderedIds: string[] } | null {
  if (draggedId === targetId) return null;
  const target = sections.find((s) => s.id === targetId);
  if (!target) return null;
  const parentId = target.parentId;
  const siblings = sections
    .filter((s) => s.parentId === parentId)
    .sort(bySiblingOrder)
    .map((s) => s.id);
  return { parentId, orderedIds: computeReorder(siblings, draggedId, targetId) };
}

/// Reparent `draggedId` as the last child of `parentId` (or to the top level when `parentId` is null).
/// Returns the `{ parentId, orderedIds }` payload for `reorderSections`.
export function appendSectionUnder(
  sections: SectionDto[],
  draggedId: string,
  parentId: string | null,
): { parentId: string | null; orderedIds: string[] } {
  const siblings = sections
    .filter((s) => s.parentId === parentId)
    .sort(bySiblingOrder)
    .map((s) => s.id);
  return { parentId, orderedIds: computeReorder(siblings, draggedId, null) };
}
