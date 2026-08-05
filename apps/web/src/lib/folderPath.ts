/// Which crumbs survive when a folder path is too long for the space it has. Pure, and separate from the
/// component, because the collapse rule is the piece most likely to need tuning against real folder names -
/// and tuning it should not mean re-testing a React tree.

export interface PathCrumb {
  id: string;
  name: string;
}

/// A rendered path is crumbs with at most one gap marker. The marker is not a crumb: it carries no id,
/// because the menu shows the full chain anyway and a second way to reach it would not earn its pixels.
export type PathSegment = PathCrumb | "ellipsis";

/// Collapse `crumbs` (root first, current last) to at most `maxVisible` crumbs by dropping from the middle.
/// The first crumb anchors the path and the **last** is the folder you are actually in, so the tail is what
/// survives a tight budget: at `maxVisible` 1 you get the current folder alone, never the root alone.
/// The ellipsis does not count against the budget.
export function collapsePath(crumbs: PathCrumb[], maxVisible: number): PathSegment[] {
  if (crumbs.length === 0) return [];

  const budget = Math.max(1, maxVisible);
  if (crumbs.length <= budget) return [...crumbs];
  if (budget === 1) return [crumbs[crumbs.length - 1]];

  // One slot for the root, the rest for the tail.
  const tail = crumbs.slice(crumbs.length - (budget - 1));
  return [crumbs[0], "ellipsis", ...tail];
}
