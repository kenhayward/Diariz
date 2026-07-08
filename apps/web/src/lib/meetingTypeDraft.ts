import type { MeetingTypeContent, TemplateSection, TemplateBlock, TemplateBlockKind } from "./types";

/// The substitutable recording fields a `field` block may use (must match the backend `MeetingTypeContent.Fields`).
export const FIELD_OPTIONS = ["date", "time", "title", "attendees", "duration", "action_items", "notes"] as const;

export function newBlock(kind: TemplateBlockKind): TemplateBlock {
  if (kind === "field") return { kind, field: "date", breakAfter: "none" };
  return { kind, text: "", breakAfter: "paragraph" };
}

export function newSection(level: 1 | 2 = 1): TemplateSection {
  return { level, title: "", blocks: [] };
}

export function emptyContent(): MeetingTypeContent {
  return { sections: [] };
}

// ---- immutable section operations ----

function withSections(content: MeetingTypeContent, sections: TemplateSection[]): MeetingTypeContent {
  return { ...content, sections };
}

export function addSection(content: MeetingTypeContent, level: 1 | 2 = 1): MeetingTypeContent {
  return withSections(content, [...content.sections, newSection(level)]);
}

export function removeSection(content: MeetingTypeContent, index: number): MeetingTypeContent {
  return withSections(content, content.sections.filter((_, i) => i !== index));
}

export function updateSection(
  content: MeetingTypeContent,
  index: number,
  patch: Partial<TemplateSection>,
): MeetingTypeContent {
  return withSections(content, content.sections.map((s, i) => (i === index ? { ...s, ...patch } : s)));
}

export function moveSection(content: MeetingTypeContent, from: number, to: number): MeetingTypeContent {
  return withSections(content, move(content.sections, from, to));
}

// ---- immutable block operations (within a section) ----

function mapSection(
  content: MeetingTypeContent,
  sectionIndex: number,
  fn: (blocks: TemplateBlock[]) => TemplateBlock[],
): MeetingTypeContent {
  return withSections(
    content,
    content.sections.map((s, i) => (i === sectionIndex ? { ...s, blocks: fn(s.blocks) } : s)),
  );
}

export function addBlock(
  content: MeetingTypeContent,
  sectionIndex: number,
  kind: TemplateBlockKind,
): MeetingTypeContent {
  return mapSection(content, sectionIndex, (blocks) => [...blocks, newBlock(kind)]);
}

export function removeBlock(content: MeetingTypeContent, sectionIndex: number, blockIndex: number): MeetingTypeContent {
  return mapSection(content, sectionIndex, (blocks) => blocks.filter((_, i) => i !== blockIndex));
}

export function updateBlock(
  content: MeetingTypeContent,
  sectionIndex: number,
  blockIndex: number,
  patch: Partial<TemplateBlock>,
): MeetingTypeContent {
  return mapSection(content, sectionIndex, (blocks) =>
    blocks.map((b, i) => (i === blockIndex ? { ...b, ...patch } : b)),
  );
}

export function moveBlock(
  content: MeetingTypeContent,
  sectionIndex: number,
  from: number,
  to: number,
): MeetingTypeContent {
  return mapSection(content, sectionIndex, (blocks) => move(blocks, from, to));
}

export function moveBlockCrossSection(
  content: MeetingTypeContent,
  from: { section: number; index: number },
  to: { section: number; index: number },
): MeetingTypeContent {
  const src = content.sections[from.section];
  if (!src) return content;
  const block = src.blocks[from.index];
  if (!block) return content;
  if (from.section === to.section) return moveBlock(content, from.section, from.index, to.index);

  const sections = content.sections.map((s, i) => {
    if (i === from.section) return { ...s, blocks: s.blocks.filter((_, bi) => bi !== from.index) };
    if (i === to.section) {
      const blocks = [...s.blocks];
      const clamped = Math.max(0, Math.min(to.index, blocks.length));
      blocks.splice(clamped, 0, block);
      return { ...s, blocks };
    }
    return s;
  });
  return { ...content, sections };
}

/// Back-fill an explicit `breakAfter` on any block that lacks one, using the legacy rule (glue only before a
/// field). Applied when loading a template so pre-feature templates show correct controls and re-render identically.
export function normalizeBreaks(content: MeetingTypeContent): MeetingTypeContent {
  return {
    ...content,
    sections: content.sections.map((s) => ({
      ...s,
      blocks: s.blocks.map((b, i) => ({ ...b, breakAfter: b.breakAfter ?? legacyBreak(s.blocks[i + 1]) })),
    })),
  };
}

function legacyBreak(next: TemplateBlock | undefined): "none" | "paragraph" {
  return next && next.kind === "field" ? "none" : "paragraph";
}

/// The first problem with the template (mirrors the backend's validation), or null when it's savable.
export function contentError(content: MeetingTypeContent): string | null {
  for (const section of content.sections) {
    if (!section.title.trim()) return "sectionTitleRequired";
    for (const block of section.blocks) {
      if (block.kind === "field") {
        if (!block.field || !FIELD_OPTIONS.includes(block.field as (typeof FIELD_OPTIONS)[number]))
          return "blockFieldRequired";
      } else if (!block.text?.trim()) {
        return "blockTextRequired";
      }
    }
  }
  return null;
}

/// Move an item within a copy of the array (out-of-range indices are clamped; returns a new array).
function move<T>(arr: T[], from: number, to: number): T[] {
  const copy = [...arr];
  if (from < 0 || from >= copy.length) return copy;
  const clampedTo = Math.max(0, Math.min(to, copy.length - 1));
  const [item] = copy.splice(from, 1);
  copy.splice(clampedTo, 0, item);
  return copy;
}
