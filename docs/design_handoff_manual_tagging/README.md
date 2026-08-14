# Handoff: Manual tags on the recording hub (Diariz)

## Overview
Diariz already auto-tags every recording (weighted topics, a Tags tab with a cloud, search and drill-down),
but there is no way to add a tag by hand. This design adds manual tagging to the **recording hub**
(`apps/web/src/pages/RecordingDetail.tsx` → `components/detail/RecordingHub.tsx`):

- a **Tags pill** on the hero summary card, sitting immediately right of the meeting-type chip (both are the
  card's actionable controls), showing the tag count and naming the first few tags on hover;
- a **popover** anchored under that pill with the three pieces: **tag entry** (one word per tag),
  **existing tags** on the recording, and the **auto-generated tags as a hint list** that can be picked
  (promoted into the user's tags) or dismissed.

Nothing is added to the page body: the tags themselves are not shown inline on the hub, only the count.

## About the Design Files
The files in this bundle are **design references created in HTML** — prototypes of the intended look and
behaviour, not production code to copy. They render the surrounding Diariz UI (left meetings panel, capture
bar, recording hub, chat rail, status bar) so the new controls can be judged in situ; that surrounding chrome
was recreated from the real repo source and is **not** to be re-implemented — it already exists.

The task is to build the Tags pill + popover in the existing web app: **React + TypeScript + Vite + Tailwind**
(`apps/web`), reusing the app's own components (`HubPopover`, `DetailChip`, `ToolbarButton`, `MeetingTypeMenu`
as the sibling control) and its i18n catalogues (`apps/web/src/locales/*`). The prototype's inline styles exist
because of the prototyping environment — in the app, use Tailwind classes with `dark:` variants, exactly as the
components listed under **Reference source** do.

## Fidelity
**High-fidelity.** Colours, type sizes, radii, paddings and interaction rules below are final and were taken
from the repo's existing components and CSS token layer. Recreate pixel-for-pixel using Tailwind + the
`--hub-*` tokens in `apps/web/src/index.css`. Both themes are specified (screenshots for each).

## Screens / Views

### 1. Recording hub — hero summary card with the Tags pill
**Purpose:** see at a glance how many tags a recording carries and get to tag entry in one click.

**Layout:** unchanged from `HeroSummaryCard.tsx`. Row 1 is
`flex flex-wrap items-center gap-2.5`: 34×34 emerald glyph tile → `Summary` heading (15px/600) →
`MeetingTypeMenu variant="pill"` → **new Tags pill** → `ml-auto` toolbar cluster (edit, re-summarise,
"Open full minutes"). The pill must be the last item before the `ml-auto` group so it reads as a pair with the
meeting-type chip.

**Tags pill**
- Shape: `inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold`
- Light: border `rgba(47,107,237,.45)`, background `rgba(47,107,237,.16)`, text `#1e40af`
- Dark: same border/background, text `#cfe0ff`
- Hover: background `rgba(47,107,237,.28)`
- Contents, in order: 14px tag glyph (Feather `tag`, stroke 2, plus a 1.5r filled dot at 7.5,7.5) ·
  label `Tags` · count in the muted tone (`#6b7280` light / `#9ca3af` dark, weight 500) · 12px chevron-down
  in `#64748b` / `#7c8aa3`
- `title` (hover text): the first **4** tags joined with ` · `, then ` · +N more` when there are more.
  With no tags: `No tags yet — click to add`. `aria-label="Tags"`, `aria-haspopup="dialog"`,
  `aria-expanded`.
- The pill stays visible while a recording is in progress — manual tagging works during capture too.

### 2. Tags popover
**Purpose:** type tags, remove tags, accept or dismiss the auto-generated suggestions.

**Shell** — reuse `components/hub/HubPopover.tsx`: `position:absolute; top: calc(100% + 8px)` relative to a
`relative` wrapper around the pill, width **392px**, radius **14px**, `animation: popIn .14s ease`, a
`fixed inset-0 z-40` click-away backdrop (`rgba(4,8,15,.45)`), Escape closes.
Background/border/shadow come from the token layer: `--hub-popover-bg`, `--hub-popover-border`,
`--hub-popover-shadow`. Body padding `16px 18px 18px`, `flex-col gap-3`.

**a. Header row** (`flex items-center gap-2.5`)
- 19px tag glyph in `#2f6bed` (light) / `#8ab0ff` (dark), inside a `19×22px` centring box so it aligns
  optically with the 16px/700 title
- Title `Tags` — 16px, weight 700, `--hub-text`
- Sub-label `saved as you type` — 11px, `--hub-muted`
- Close button on `margin-left:auto` — 28×28, radius 8, 16px ✕, `--hub-muted`, hover background
  `--hub-surface-hover`

**b. Token entry field** (the tag entry + existing tags, one control)
- Container: `flex flex-wrap items-center gap-1.5`, min-height 46px, padding 8px, radius 10px,
  border `rgba(15,23,42,.14)` light / `rgba(255,255,255,.14)` dark, background `--hub-surface`,
  `cursor:text`; clicking anywhere in it focuses the input
- Each existing tag is a chip: height **28px**, padding `0 4px 0 10px`, radius 8px, background
  `rgba(47,107,237,.16)`, border `rgba(47,107,237,.35)`, text 12.5px/500 (`#1e40af` light / `#cfe0ff` dark),
  followed by a remove button — **22×22 hit area, 15px ✕ (stroke 2.2)**, radius 6, hover background
  `rgba(…,.1)` and text to full contrast. `aria-label="Remove tag"`.
- Input: flex-1, min-width 96px, height 26px, transparent, 12.5px, placeholder `Add a tag…`,
  `aria-label="Add a tag"`
- Hint line under the field: 11.5px `--hub-muted` —
  `Space or Enter adds the word · pasting a phrase joins it with hyphens · Backspace removes the last tag`

**c. Divider** 1px `rgba(15,23,42,.08)` / `rgba(255,255,255,.08)`

**d. Auto-generated hints**
- Label row: `AUTO-GENERATED · PICK OR IGNORE` — 11px, weight 700, `letter-spacing:.08em`, uppercase,
  `--hub-muted`; on `ml-auto` a count `N left` in `--hub-muted-2`
- Chips wrap in `flex flex-wrap gap-1.5`. Each hint is a 26px pill with a **dashed** border
  (`rgba(…,.2)`), transparent background, containing two controls:
  - **Add** (the whole label): `+` glyph 11px in `#2f6bed` / `#8ab0ff` then the tag text 12.5px in
    `#334155` / `#c7d0e0`; hover background `rgba(…,.07)`; `title="Add this tag"`
  - **Dismiss**: 18×18, 10px ✕, `--hub-muted-2`, hover background `rgba(…,.08)` and text `--hub-red-text`;
    `title="Never suggest this"`
- When every hint has been dealt with, the row shows `All suggestions dealt with.` (12px, `--hub-muted-2`)

## Interactions & Behavior
- **Open/close:** clicking the pill toggles the popover; backdrop click and Escape close it (HubPopover
  already does both). Only one hub popover may be open at a time — go through `useHubPopover()`
  (`components/hub/hubPopovers.tsx`) if the pill joins that family.
- **Adding a tag:** a **space** commits the current word; **Enter** commits it *and closes the popover*
  ("done"); focus stays in the field after a space so several tags can be typed in a run.
- **Phrases:** a tag never contains a space. On **paste**, internal whitespace is collapsed to `-`
  (`"budget planning 2026"` → `budget-planning-2026`); leading/trailing hyphens are trimmed.
- **Duplicates:** de-duplicated **case-insensitively**, but stored as typed (`Metadata` typed second when
  `metadata` exists is a no-op).
- **Backspace** on an empty input removes the last tag.
- **Removing:** the chip's ✕ removes that tag immediately (no confirm).
- **Picking a hint:** the hint is **promoted** — it becomes a normal tag in the field and leaves the hint
  list. Its `N left` count drops.
- **Dismissing a hint:** removed from the hint list permanently (persist per recording, or per user if you
  prefer a global "never suggest" list — the design only requires it not to come back).
- **Saving:** no Save button; each add/remove/promote/dismiss persists on its own (optimistic, with the
  existing `apiErrorMessage` treatment on failure). The header's `saved as you type` states this.
- **Type-ahead (optional, specified in option 1b of the v1 file):** suggestions drawn from tags the user has
  used across their library. Not part of the chosen design's must-haves, but the entry field is sized for it.
- **Animation:** popover entry only — `popIn .14s ease` (already in `index.css`).
- **Empty state:** no tags yet → the field shows just the placeholder; the pill shows a `0` count and the
  hover text `No tags yet — click to add`.
- **Scale:** designed for ~10 manual tags; the field wraps to as many rows as needed and the popover grows.
  If a recording carries many more, the row-list variant in the v1 file (option 1c) is the fallback layout.

## State Management
Local to the pill/popover component:
- `open: boolean` — popover visibility
- `draft: string` — the current word in the input
- `tags: string[]` — the recording's manual tags (server-backed; seed from the recording detail)
- `hints: string[]` — auto tags not yet picked or dismissed (server-backed; the recording's auto tags minus
  promoted/dismissed)

Server/data work needed:
- read the recording's manual tags + its auto tags with the recording detail (`RecordingDetail`)
- add / remove a manual tag; mark an auto tag promoted or dismissed
- invalidate the `["tags", roomId]` query (the Tags tab's cloud, see `nav/TagsTab.tsx`) after any change, and
  the recording detail query, so the cloud and the pill's count stay in step
- webhooks/automations already emit `recording.tags_ready`; decide whether manual edits fire an event too

## Design Tokens
Existing token layer (`apps/web/src/index.css`, `:root` and `.dark`) — use these, don't add new ones:

| Token | Light | Dark |
| :--- | :--- | :--- |
| `--hub-popover-bg` | `#ffffff` | `#0e1729` |
| `--hub-popover-border` | `rgba(15,23,42,.1)` | `rgba(255,255,255,.11)` |
| `--hub-popover-shadow` | `0 16px 40px rgba(15,23,42,.16)` | `0 24px 60px rgba(0,0,0,.6)` |
| `--hub-surface` | `#f1f5f9` | `#131d31` |
| `--hub-surface-hover` | `rgba(15,23,42,.05)` | `rgba(255,255,255,.06)` |
| `--hub-text` | `#0f172a` | `#eef2f8` |
| `--hub-text-2` | `#334155` | `#c7d0e0` |
| `--hub-muted` | `#64748b` | `#7c8aa3` |
| `--hub-muted-2` | `#94a3b8` | `#6b7890` |
| `--hub-blue` | `#2f6bed` | `#2f6bed` |
| `--hub-blue-soft-bg` | `rgba(47,107,237,.1)` | `rgba(47,107,237,.14)` |
| `--hub-blue-soft-border` | `rgba(47,107,237,.3)` | `rgba(47,107,237,.3)` |
| `--hub-blue-text` | `#1e40af` | `#cfe0ff` |
| `--hub-red-text` | `#dc2626` | `#ff8b8f` |

Tag-specific values (the design's own): chip fill `rgba(47,107,237,.16)`, chip border `rgba(47,107,237,.35)`,
pill border `rgba(47,107,237,.45)`, pill hover `rgba(47,107,237,.28)`, hint border `1px dashed rgba(…,.2)`.

**Spacing:** popover padding `16px 18px 18px`; section gap 12px; chip gap 6px; field padding 8px.
**Radii:** popover 14px · field 10px · tag chip 8px · hint chip 7px · buttons 5–6px · pill fully rounded.
**Type:** system-ui stack (`system-ui, -apple-system, Segoe UI, Roboto, sans-serif`) —
popover title 16/700 · tag chip 12.5/500 · hint chip 12.5/400 · section label 11/700 uppercase `.08em` ·
helper text 11.5/400 · pill 12/600.
**Sizes:** pill height ~26px · tag chip 28px · hint chip 26px · remove ✕ 15px in a 22px box ·
hint dismiss ✕ 10px in an 18px box · popover width 392px.

## Assets
No new assets. Icons are inline Feather-style SVG on the 24 grid (`fill:none`, `stroke:currentColor`,
`stroke-width:2`, round caps/joins) matching `components/detail/SectionIcons.tsx` and `components/icons.tsx`.
The only new glyph is the **tag** shape:
`M20.6 13.4 12.6 21.4a2 2 0 0 1-2.8 0l-7-7a2 2 0 0 1-.6-1.4V4a2 2 0 0 1 2-2h8a2 2 0 0 1 1.4.6l7 7a2 2 0 0 1 0 2.8z`
plus `<circle cx="7.5" cy="7.5" r="1.3" fill="currentColor"/>`. Add it to `components/icons.tsx` as
`TagIcon({ size })` so it sizes and strokes with the rest.

## Reference source (what the surrounding UI already is)
`components/detail/HeroSummaryCard.tsx` (the card the pill goes in) · `components/detail/DetailChip.tsx` ·
`components/MeetingTypeMenu.tsx` (the pill's neighbour) · `components/hub/HubPopover.tsx` (popover shell) ·
`components/hub/NotesPopover.tsx` (the closest existing popover for tone) · `components/hub/hubPopovers.tsx` ·
`components/detail/RecordingHub.tsx` · `components/detail/DetailHeader.tsx` · `components/TagCloud.tsx` ·
`components/nav/TagsTab.tsx` · `lib/tagCloud.ts` · `index.css`.

## Files
| File | What it is |
| :--- | :--- |
| `Manual Tagging.dc.html` | The chosen design, dark theme, in situ on the recording hub. Interactive: type, space/Enter, paste, remove, promote, dismiss. |
| `Manual Tagging -light-.dc.html` | The same design in the light theme. |
| `Manual Tagging v1 (three options).dc.html` | The earlier option board — 1a (chosen), 1b (header-cluster button + type-ahead), 1c (row-list popover for many tags). Useful for the type-ahead and many-tag fallbacks. |
| `support.js` | Runtime needed to open the `.dc.html` files in a browser. Not application code. |
| `screenshots/recording-hub-tags-light.png` | Light theme, popover open. |
| `screenshots/recording-hub-tags-dark.png` | Dark theme, popover open. |

Open either `.dc.html` directly in a browser (with `support.js` beside it) to try the interaction.
