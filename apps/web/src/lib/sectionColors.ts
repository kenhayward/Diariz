/// Colour assignment for sections (folders). Sections have no stored colour (unlike rooms and meeting
/// types, which carry a user-chosen one), so the left nav derives one — the same problem `speakerColors`
/// solves, with one deliberate difference: the colour is hashed from the section's **id**, not dealt out
/// by sorted index. A section's colour must not change when a sibling is created, renamed or deleted, and
/// index-dealing would re-colour half the panel every time someone adds a folder.
///
/// Each entry is a light/dark pair rather than a single swatch: the design's palette is deepened for a
/// white background (a folder glyph in `#38bdf8` is illegible on white), so unlike speaker swatches these
/// are not identical in both themes. Literal hex, not Tailwind classes — they are data, and callers pick
/// the arm for the active theme.

export interface SectionColor {
  light: string;
  dark: string;
}

export const SECTION_PALETTE: readonly SectionColor[] = [
  { light: "#0891b2", dark: "#38bdf8" }, // cyan
  { light: "#7c3aed", dark: "#a78bfa" }, // purple
  { light: "#0ea371", dark: "#34d399" }, // green
  { light: "#d97706", dark: "#fbbf24" }, // amber
  { light: "#db2777", dark: "#f472b6" }, // pink
];

/// FNV-1a (32-bit). A hash, not a sum: section ids are GUIDs that share long common prefixes, and a
/// cheaper char-sum collapses them onto only a couple of palette entries. `>>> 0` keeps the value
/// unsigned after the multiply overflows into the sign bit.
function hash(id: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < id.length; i++) {
    h ^= id.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h;
}

/// The colour for a section id. Stable for the life of the section, and total — an empty or unknown id
/// hashes like any other string rather than throwing, so a synthetic node (a recording pointing at a
/// section the list hasn't loaded yet) still renders.
export function sectionColor(id: string): SectionColor {
  return SECTION_PALETTE[hash(id) % SECTION_PALETTE.length];
}
