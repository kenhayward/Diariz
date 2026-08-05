/// Projections of the recordings tree for the left nav's drill-in list, which shows **one level at a
/// time** rather than the whole expanded tree. `buildRecordingTree` still owns the shape (and the
/// load-order safety net for unknown section ids); this module only answers the three questions the
/// drill-in UI asks: what is at this level, how did I get here, and how much is under each row.
///
/// Everything here walks `parentId` generically, so it works at any depth. The API caps folders at
/// `MAX_FOLDER_DEPTH` levels; only `sectionCreateTarget` below knows that.

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

/// How deep folders may nest, mirroring `SectionTree.MaxDepth` on the API. Top level is 1, so this is the
/// number of folder levels. Kept in sync by hand: the two constants are in different languages, and the
/// server is the one that enforces it - this copy only decides what the UI offers.
export const MAX_FOLDER_DEPTH = 8;

/// The level a folder sits at: 0 for the room root, 1 for a top-level folder. Zero for an unknown id.
/// Guards against a `parentId` cycle, which nothing in the schema prevents.
export function depthOf(sections: SectionDto[], sectionId: string | null): number {
  if (sectionId === null) return 0;
  const byId = new Map(sections.map((s) => [s.id, s]));
  let current = byId.get(sectionId);
  if (!current) return 0;

  let depth = 1;
  const seen = new Set<string>([sectionId]);
  while (current.parentId && !seen.has(current.parentId)) {
    seen.add(current.parentId);
    const parent = byId.get(current.parentId);
    if (!parent) break;
    current = parent;
    depth++;
  }
  return depth;
}

/// How many levels the subtree rooted at a folder spans, counting the folder itself as 1 - the branch-move
/// counterpart to `depthOf`. Mirrors `SectionTree.Height` on the API: a leaf is 1, and an id no longer in
/// `sections` is also 1 (it occupies one level regardless, matching how a caller composes
/// `depthOf(target) + heightOf(moved) <= MAX_FOLDER_DEPTH`; existence is validated upstream). Guards against
/// a `parentId` cycle, which nothing in the schema prevents.
export function heightOf(sections: SectionDto[], sectionId: string): number {
  const byId = new Map(sections.map((s) => [s.id, s]));
  if (!byId.has(sectionId)) return 1;

  // Collect the whole subtree level by level - the same cycle-safe walk as childrenOf's recursive find, just
  // iterative so a cycle among descendants can't loop it forever.
  const seen = new Set<string>([sectionId]);
  let frontier = [sectionId];
  while (frontier.length > 0) {
    const next: string[] = [];
    for (const s of sections) {
      if (s.parentId === null || seen.has(s.id) || !frontier.includes(s.parentId)) continue;
      seen.add(s.id);
      next.push(s.id);
    }
    frontier = next;
  }

  const rootDepth = depthOf(sections, sectionId);
  let deepest = rootDepth;
  for (const id of seen) {
    const d = depthOf(sections, id);
    if (d > deepest) deepest = d;
  }
  return deepest - rootDepth + 1;
}

/// Where a new folder created from the toolbar should go, given where you are browsing. `blocked` covers
/// both ends of the same problem: the drill is at the depth cap (`SectionsController.Create` would 400) or
/// inside an id that is no longer in the tree (deleted from another tab - the level renders empty, and
/// creating under a ghost parent would only 404).
export type SectionCreateTarget =
  | { kind: "root" }
  | { kind: "child"; parent: SectionDto }
  | { kind: "blocked" };

/// Unlike the rest of this module, this one **does** encode the depth cap - it has to, because it decides
/// what the API will accept.
export function sectionCreateTarget(
  sections: SectionDto[],
  sectionId: string | null,
): SectionCreateTarget {
  if (sectionId === null) return { kind: "root" };
  const parent = sections.find((s) => s.id === sectionId);
  if (!parent) return { kind: "blocked" };
  if (depthOf(sections, sectionId) >= MAX_FOLDER_DEPTH) return { kind: "blocked" };
  return { kind: "child", parent };
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
