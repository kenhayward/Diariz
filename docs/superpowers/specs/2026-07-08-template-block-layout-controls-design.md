# Template block layout controls + richer block editing

**Date:** 2026-07-08
**Component:** Meeting-types template editor (`ManageMeetingTypesModal`) + minutes composer
**Version bump:** 0.108.1 -> 0.109.0 (functional enhancement: Minor +1, Build 0)
**Deployment surface:** web + API -> server redeploy only. No desktop release.

## Problem

The "Manage Meeting Types" template editor builds each section from ordered blocks of kind
`boilerplate` (the UI's "text"), `field`, or `prompt`. Today the whitespace between blocks is
hard-coded in the server composer:

- a `field` block glues inline to whatever precedes it (so `"Date: "` + `date` reads as one line),
- `boilerplate` and `prompt` blocks always start their own paragraph.

This gives users no control: a `text` then `prompt` sequence always breaks, a `text` then `field`
sequence never does. Users want to decide, per block, whether a break follows and how big it is.

Two adjacent editing pains: the block text input is a fixed-height textarea (awkward for longer
markdown), and blocks can only be reordered up/down within their own section (no way to move a block
into a different section).

## Goals

1. A per-block control choosing the break **after** that block: none / single line break / paragraph
   break. Applies to all three kinds (`boilerplate`, `field`, `prompt`).
2. The block text input becomes an auto-growing textarea; text is raw markdown, stored verbatim (no
   preview, no WYSIWYG).
3. A drag handle on each block to move it within a section and between sections, using the same
   native HTML5 drag-and-drop the section reorder already uses (no new dependency).

## Non-goals

- No WYSIWYG/preview for block text (explicitly rejected during brainstorming).
- No DnD library (`@dnd-kit` etc.) - native HTML5 DnD only, consistent with existing section reorder.
- No change to how prompts are resolved, how fields are substituted, or the section/heading model.

## Design

### 1. Data model: a per-block `breakAfter`

Add one optional field to `TemplateBlock` on both sides of the .NET <-> TS boundary. It serializes as
camelCase `breakAfter` under the existing `JsonSerializerDefaults.Web` options, so it round-trips
through the `MeetingType.ContentJson` blob with **no migration** - old blocks simply lack it.

- **TS** (`apps/web/src/lib/types.ts`):
  `breakAfter?: "none" | "line" | "paragraph" | null` on `TemplateBlock`.
- **C#** (`src/Diariz.Api/Services/MeetingTypeContent.cs`): add `string? BreakAfter = null` to the
  `TemplateBlock` record, with constants `BreakNone = "none"`, `BreakLine = "line"`,
  `BreakParagraph = "paragraph"`. `MeetingTypeContent.Validate()` rejects any non-null value not in
  that set (null is always valid = legacy).

### 2. Composer: honor `breakAfter`, stay backward-compatible

Rewrite `RenderBlocksAsync` in `MeetingTypeMinutesComposer.cs`. Render each block to a string
(`boilerplate` -> `Text`; `field` -> `resolveField`; `prompt` -> trimmed `resolvePrompt`), **drop
blocks that render empty**, then join adjacent rendered blocks with a separator chosen by the
**preceding** rendered block's effective break:

| effective `breakAfter` | separator |
|---|---|
| `none`      | `""`     (glue inline) |
| `line`      | `"\n"`   (single line break; renders as `<br>` via the app's `marked({ breaks: true })`) |
| `paragraph` | `"\n\n"` (blank line) |

**Effective break** of block `i` = `block[i].BreakAfter` when non-null; otherwise the **legacy rule**:
glue (`none`) iff the next rendered block is a `field`, else `paragraph`. This reproduces today's
output exactly for templates saved before this feature, so every existing composer test passes
unchanged.

Section-level assembly is unchanged: sections are joined with `"\n\n"`, a heading is
`"#"*level + " " + title`, a section whose blocks all render empty is dropped, and the whole result is
trimmed.

`"line"` uses a single `\n` (relies on the app-wide `breaks: true` marked config, consistent with the
rest of the app) rather than a CommonMark hard break (`"  \n"`); trailing-space hard breaks are
fragile and easily stripped.

### 3. UI: `ManageMeetingTypesModal.tsx` `BlockRow`

- **Break-after control** on every block kind: a compact `<select>` with options
  None / Line break / Paragraph, `aria-label="Break after"` (localized), placed before the kebab,
  wired to `updateBlock(content, si, bi, { breakAfter })`.
- **Auto-growing textarea** for `boilerplate`/`prompt`: a small local `AutoGrowTextarea` component
  that sets its height to `scrollHeight` on input (and on mount for seeded content), replacing the
  fixed `rows`. Raw markdown, stored verbatim as today, with a subtle "Markdown supported" hint.
  `field` blocks keep their `<select>`.
- **Drag handle** (the same `U+283F` glyph the section header uses) at the left of each row, `draggable`.
  A `dragBlock` ref at the `ContentEditor` level holds `{ section, index }`; `onDragStart` sets it and
  clears the section-drag ref. Drop targets:
  - each block row -> insert the dragged block **before** that row's position;
  - each section's block-list container -> **append** to that section (so a block can be dropped into
    an empty section or at a section's end).

  On drop, call `moveBlockCrossSection(content, dragBlock, target)` and clear the ref. The section-drag
  and block-drag refs are distinct, and each drop handler acts only when its own ref is set, so the two
  drag interactions never cross-fire. The kebab's Move up / Move down / Delete (within-section) stay.

### 4. Draft lib: `apps/web/src/lib/meetingTypeDraft.ts`

- `newBlock(kind)` sets a default `breakAfter`: `boilerplate`/`prompt` -> `"paragraph"`,
  `field` -> `"none"`.
- `moveBlockCrossSection(content, from, to)` where `from`/`to` are `{ section, index }`: immutable
  remove-from-source then insert-into-destination, indices clamped; a same-section move delegates to the
  existing `move` helper. Existing `moveBlock` (within-section, used by the kebab) stays.
- `normalizeBreaks(content)`: back-fill an explicit `breakAfter` on any block that lacks one, using the
  legacy rule (`next block is a field ? "none" : "paragraph"`). Applied inside `draftFrom` so opening a
  pre-feature template shows correct control values and re-saving preserves its rendering.

### 5. Testing (TDD - failing test first)

- **Composer** (`tests/Diariz.Api.Tests/MeetingTypeMinutesComposerTests.cs`): a test per `breakAfter`
  value (`none` inline, `line` single `\n`, `paragraph` blank line); a test that a null `breakAfter`
  falls back to legacy field-glue; existing tests remain green untouched.
- **Content validation** (`MeetingTypeContent.Validate`): `BreakAfter` accepts the three values + null,
  rejects an unknown value.
- **Draft** (`apps/web/src/lib/meetingTypeDraft.test.ts`): `newBlock` defaults per kind;
  `moveBlockCrossSection` within-section, across-section, into an empty section, and index clamping;
  `normalizeBreaks` legacy mapping.
- **Component** (`apps/web/src/components/ManageMeetingTypesModal.test.tsx`): the break-after `<select>`
  renders for a block and updates the draft; an auto-growing textarea is present for text blocks. Native
  DnD is not jsdom-testable, so cross-section moves are covered through the pure `moveBlockCrossSection`.
- **Markdown** (optional): confirm a single `\n` renders a `<br>` (already implied by `breaks: true`).

### 6. Versioning & docs (per CLAUDE.md)

- Bump `version.json` 0.108.1 -> **0.109.0** in lockstep with `apps/web/package.json`,
  `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` `<Version>`, and add a
  `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` (version, date, pr, headline, prose summary,
  `added`/`changed` bullets). No em/en dashes in the notes.
- **`docs/Data_Schema.md`**: the meeting-type content JSON (`TemplateBlock`) gains `breakAfter`.
- **`docs/features.md` + README Features row + About `CAPABILITIES`**: refresh the minutes-template line
  to mention per-block layout control and cross-section reordering (lockstep, one concise line each).
- No architecture change, so `Overall_Synopsis_of_Platform.md` is untouched.
- Deployment surface stated in the PR: **server redeploy only, no desktop release**.

## Risks / edge cases

- **`"line"` portability:** relies on `marked({ breaks: true })`. If minutes markdown is ever consumed by
  a strict CommonMark renderer (e.g. a future email/PDF path), single `\n` collapses to a space. Accepted;
  revisit with `"  \n"` only if such a consumer appears.
- **Gluing a `prompt` inline (`none`):** prompt output is multi-line markdown; gluing it directly to a
  neighbour can read oddly. It is the user's explicit choice, so allowed, not prevented.
- **Back-fill on load rewrites blobs:** opening + saving a legacy template persists explicit `breakAfter`
  on every block. Output is identical (explicit == legacy), so this is a no-op in rendering.
