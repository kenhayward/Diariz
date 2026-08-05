import type { SectionDto } from "./types";

/// One flattened row for a folder picker: the section itself, its full-path label ("Parent › Child"), and
/// its depth (top level = 1) so a future indented picker can use it instead of the concatenated label.
export interface OrderedSection {
  section: SectionDto;
  label: string;
  depth: number;
}

const byParent = (sections: SectionDto[], parentId: string | null) =>
  sections
    .filter((s) => (s.parentId ?? null) === parentId)
    .sort((a, b) => (a.position ?? 0) - (b.position ?? 0) || a.name.localeCompare(b.name));

/// Flatten the whole folder tree for a picker: every folder, at any depth, each one immediately followed
/// by its own descendants (display order), labelled with its full path. Both existing consumers
/// (`MoveToSectionModal`, `RecordingsSection`) only see top-level folders and their direct children today,
/// which makes anything nested deeper unreachable now that folders can go 8 levels deep - this walks the
/// whole tree so every folder is selectable.
///
/// Guards against a `parentId` cycle with a visited set, as the other tree walks in this codebase do
/// (`drillView.ts`'s `breadcrumbOf`/`depthOf`) - nothing in the schema prevents one.
export function orderedSections(sections: SectionDto[]): OrderedSection[] {
  const out: OrderedSection[] = [];

  const walk = (parentId: string | null, parentLabel: string, depth: number, seen: ReadonlySet<string>) => {
    for (const s of byParent(sections, parentId)) {
      if (seen.has(s.id)) continue; // parentId cycle - stop rather than spin
      const label = parentLabel ? `${parentLabel} › ${s.name}` : s.name;
      out.push({ section: s, label, depth });
      walk(s.id, label, depth + 1, new Set(seen).add(s.id));
    }
  };

  walk(null, "", 1, new Set());
  return out;
}
