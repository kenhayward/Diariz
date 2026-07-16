/// Projections of the recordings tree for the left nav's drill-in list, which shows **one level at a
/// time** rather than the whole expanded tree. `buildRecordingTree` still owns the shape (and the
/// load-order safety net for unknown section ids); this module only answers the three questions the
/// drill-in UI asks: what is at this level, how did I get here, and how much is under each row.
///
/// Everything here walks `parentId` generically. The domain caps sections at two levels (enforced in
/// `SectionsController`), but nothing below assumes that — lifting the cap should not touch the nav.

import type { RecordingTree, SectionNode } from "./recordingTree";
import type { RecordingSummary, SectionDto } from "./types";

/// One rung of the drill: the subsections shown as folder rows, and the recordings filed *directly*
/// here (the design's "DIRECTLY IN <SECTION>" block). Recordings deeper down are not included — that is
/// the point of drilling.
export interface DrillLevel {
  sections: SectionNode[];
  items: RecordingSummary[];
}

const EMPTY: DrillLevel = { sections: [], items: [] };

function find(nodes: SectionNode[], id: string): SectionNode | undefined {
  for (const node of nodes) {
    if (node.id === id) return node;
    const hit = find(node.children, id);
    if (hit) return hit;
  }
  return undefined;
}

/// The level to render for a drill position. `null` is the root: top-level sections, with the ungrouped
/// recordings as its direct items — the root is just a node like any other, which is why the drill-in
/// list has no "Ungrouped" special case. An unknown id (a section deleted while drilled into it) yields
/// an empty level rather than throwing; the caller shows the empty state.
export function childrenOf(tree: RecordingTree, sectionId: string | null): DrillLevel {
  if (sectionId === null) return { sections: tree.sections, items: tree.ungrouped };
  const node = find(tree.sections, sectionId);
  return node ? { sections: node.children, items: node.items } : EMPTY;
}

/// The ancestor chain for the breadcrumb, root-first and including the node itself. Empty at the root or
/// for an unknown id. Guards against a `parentId` cycle (nothing in the schema prevents one) — a cycle
/// would otherwise spin forever and hang the panel.
export function breadcrumbOf(sections: SectionDto[], sectionId: string | null): SectionDto[] {
  if (sectionId === null) return [];
  const byId = new Map(sections.map((s) => [s.id, s]));
  const chain: SectionDto[] = [];
  const seen = new Set<string>();
  let current = byId.get(sectionId);
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    chain.push(current);
    current = current.parentId ? byId.get(current.parentId) : undefined;
  }
  return chain.reverse();
}

function countNode(node: SectionNode): number {
  return node.items.length + node.children.reduce((n, child) => n + countNode(child), 0);
}

/// Recordings under a section **including** its subsections' — the count badge on a folder row promises
/// what you'll find by drilling in, so a section whose recordings all live one level down must not read 0.
export function recordingCountOf(tree: RecordingTree, sectionId: string): number {
  const node = find(tree.sections, sectionId);
  return node ? countNode(node) : 0;
}
