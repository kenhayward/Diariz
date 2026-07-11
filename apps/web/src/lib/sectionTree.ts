import type { SectionDto } from "./types";

/// Flatten sections for a picker: each top-level section, immediately followed by its sub-sections, which are
/// labelled "Parent › Child" so the hierarchy is clear in a flat list.
export function orderedSections(sections: SectionDto[]): { section: SectionDto; label: string }[] {
  const tops = sections
    .filter((s) => !s.parentId)
    .sort((a, b) => (a.position ?? 0) - (b.position ?? 0) || a.name.localeCompare(b.name));
  const childrenOf = (id: string) =>
    sections
      .filter((s) => s.parentId === id)
      .sort((a, b) => (a.position ?? 0) - (b.position ?? 0) || a.name.localeCompare(b.name));
  const out: { section: SectionDto; label: string }[] = [];
  for (const top of tops) {
    out.push({ section: top, label: top.name });
    for (const child of childrenOf(top.id)) out.push({ section: child, label: `${top.name} › ${child.name}` });
  }
  return out;
}
