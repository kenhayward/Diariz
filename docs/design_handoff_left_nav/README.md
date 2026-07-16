# Handoff: Diariz Left-Panel Navigation (drill-in × search × rooms)

## Overview
Redesign of the **left navigation panel** in Diariz (`apps/web`) — the pane that lists recordings organised into **sections** and **subsections** (e.g. *Customers / Ambu*). The current panel is a single always-expanded tree; once a section holds hundreds of recordings (and some accounts will soon hit thousands) it scrolls endlessly and you lose your place.

This design replaces the flat tree with a **drill-in (push) model + a persistent, scope-aware search**, sitting under a **room switcher**:

- **Drill-in columns** — the panel shows one level at a time. Tapping a section pushes into it (subsections + recordings directly in that section); a breadcrumb/back control walks back out. Depth is effectively unlimited because the list never grows past one screen of items.
- **Persistent search** — a search bar is always pinned. Typing **takes over the list body** with results; clearing it drops you back exactly where you were drilling (breadcrumb is preserved). Search is **scoped to the current folder by default** (`in Customers`) with a one-tap **Search everywhere** that promotes to a global, section-grouped, filterable result set.
- **Rooms** — the header shows the current **room** (e.g. *Platform Administrator*) and doubles as a **switcher**. Rooms are the top of the hierarchy: **Room ▸ Section ▸ Subsection ▸ Recording**. Every room has the same structure. Personal vs shared rooms are distinguished by a person / group icon.

The **end result is unchanged**: navigation still lands the user on a **section page**, a **subsection page**, or a **transcript (recording) page** in the main panel — this only changes *how* they get there.

## Scope (important)
This handoff covers **only the left navigation panel**: the room-switcher header, the search bar, the drill-in list, and the search-results view. It does **not** change the main/centre panel (that was the previous handoff — the recording-detail hub) nor the global app top bar. Section-page / subsection-page / transcript-page destinations already exist or are covered elsewhere; here we only wire the left panel to route to them.

## About the Design Files
`Diariz Left Nav.html` is a **design reference created in HTML** — a prototype of intended look and behaviour, **not production code to copy directly**. Recreate it in the existing `apps/web` stack: **React + TypeScript + Vite + Tailwind CSS v4 (class-based dark mode) + react-i18next + @tanstack/react-query + react-router-dom**. Reuse existing components/hooks; do not introduce a new styling system.

> The HTML uses inline `style="…"` and, for the dark frames, CSS variables (`--bg`, `--accent`, …) purely so the prototype renders standalone. In the app, translate to **Tailwind classes** matching the Design Tokens below. Both **light and dark** frames are included because the app supports class-based dark mode — the two must be visually paired. Icons are inline `<svg>` (lucide-style) — reuse the repo's existing inlined icon components.

## Fidelity
**High-fidelity.** Final colours, type, spacing, iconography, layout. All copy/counts in the mock are placeholder sample data — bind to real room/section/recording data.

## Views / States (see the reference HTML)

### A. Idle — drilled into a section
- **Room header** (button, full width): avatar + room name (uppercase, 12px/750, letter-spacing .04em) + chevron-down (opens switcher) + a collapse/back caret on the far right (existing panel-collapse control).
- **Search bar**: rounded field; leading search icon; a **scope chip** (`in <Section>`, accent-tinted pill with a folder glyph) that reflects the current drill location; placeholder “Search…”.
- **Breadcrumb row**: a back button (square, 28px) + two-line label — muted parent (`All sections`) above the current node (`▸ Customers`, 13.5px/650, coloured folder glyph) — and a right-aligned **“Open section page ›”** link (this is the affordance that lands on the *section page* itself).
- **List body**:
  - **Subsection rows** first: coloured folder glyph + name (12.5px/650, section colour) + count + chevron-right (drills deeper → *subsection page* / next level).
  - Divider, then a `DIRECTLY IN <SECTION> · N` label.
  - **Recording rows**: green mic glyph + title (ellipsised) + duration (`mm:ss`, tabular-nums). Tapping routes to the **transcript/recording page**.

### B. Room switcher open
- Dropdown anchored under the header (`YOUR ROOMS` label): the **current room** (checkmark) with its avatar; **other rooms** each with a rounded-square icon — **person** glyph = personal room, **group** glyph = shared room — plus name and a muted count line (`N sections · M recordings`, shared rooms prefixed `shared ·`).
- A divider then **Manage Rooms** (home glyph).
- Selecting a room **resets the panel to that room's top level** (`All sections`) and the search scope chip follows.

### C. Search — scoped to the current folder (typing)
- Search field is focused (accent ring). Below it a small summary row: `N in <Section>` on the left, **“Search everywhere ›”** on the right.
- Results are **recording hits** (mic + title + duration, plus a **matched transcript snippet** with the query term highlighted, and a **breadcrumb** `Section › Subsection` so a hit still tells you where it lives) **and folder hits** (a matching subsection row that just drills you there). Tapping a recording hit → transcript page.

### D. Search — everywhere
- Scope chip switches to a globe **Everywhere**. **Filter chips** appear: **Section**, **Date**, **Speaker**.
- Results are **grouped by section** (coloured section header + count), each group listing its recording hits with snippets. Spans every room the user can see when promoted from the room level (see Behaviour).

## Interactions & Behaviour
- **Drill in**: tapping a section/subsection row (not its count) pushes one level deeper and appends to the breadcrumb; the back button / breadcrumb pops levels. Consider `history`/route state so browser back also pops a level.
- **Section vs drill**: the row body drills in; the **“Open section page ›”** link (and tapping the current breadcrumb node) opens the **section page** in the main panel. Keep these two targets distinct (row = browse deeper, link = open the page).
- **Search takeover**: focusing/typing swaps the list body for results **without** changing the breadcrumb; clearing (`×` / empty query / `Esc`) restores the exact drill state. Debounce the query; results are async (react-query).
- **Scope**: defaults to the current folder (`in <Section>` — fast, few results). **Search everywhere** promotes to global; if invoked at a room's top level, “everywhere” spans **all rooms the user can access**, and each result carries its room + section breadcrumb.
- **Filters** (everywhere only): Section / Date / Speaker narrow the global result set.
- **Rooms**: header is the switcher; changing room reloads the drill-in at that room's `All sections` and updates the scope chip. Persist the active room (and ideally the active drill path) so reload/return restores context.
- **Keyboard (nice-to-have)**: `⌘K` / `Ctrl-K` focuses search; `Esc` clears and returns to drill; arrow keys move the highlighted row, `Enter` opens.
- **Responsive / collapse**: the panel keeps its existing collapse control (caret in the header). Two-pane variants were explored but **drill-in is the chosen model** — single column.

## State Management
- `currentRoomId` (persisted) + rooms list (react-query `["rooms"]` or existing source).
- `drillPath: string[]` — section → subsection ids currently pushed (drives breadcrumb + list query). Persist last path per room.
- `query: string`, `searchScope: 'folder' | 'everywhere'`, `filters: { section?, date?, speakerId? }`.
- List query: children of the current node (subsections + recordings directly under it) with counts.
- Search query: async, scoped by `searchScope` + `drillPath` + `filters`; returns recording hits (with snippet + breadcrumb) and folder hits.
- Derived counts per section/subsection for the badges.

## Design Tokens

### Dark (existing app dark mode)
- Panel bg `#111827`; deep/header bg `#0b1018`; surface `#182130`; surface-2 `#1f2a3b`; line `#26303f`; line-2 `#323d4e`.
- Text `#e9edf4`; muted `#9aa5b6`; faint `#6a7688`.
- Accent `#3b82f6`, accent-strong `#2563eb`, accent-soft `#16305a`; link `#7cb0ff`.
- Section colours: cyan `#38bdf8`, purple `#a78bfa`, green `#34d399`, amber `#fbbf24`, pink `#f472b6`. Recording mic = green. Snippet highlight = amber text on `rgba(251,191,36,.12)`.

### Light
- Panel bg `#ffffff`; header bg `#fbfcfe` (active `#eef3fb`); surface `#f3f5f9`; line `#e6eaf0`; line-2 `#dce3ec`.
- Text `#1b2432`; muted `#5a6675`; faint `#8a94a4` / `#93a0b0`.
- Accent `#2563eb`; accent-soft bg `#e7f0ff` (border `rgba(37,99,235,.28)`); link `#2563eb`.
- Section colours (deepened for white bg): cyan `#0891b2`, purple `#7c3aed` (text `#6d28d9`), green `#0ea371`, amber `#d97706`, pink `#db2777`. Snippet highlight = `#b45309` text on `rgba(217,119,6,.14)`.
- Room icons (both modes): personal/shared tile uses a violet gradient `linear-gradient(135deg,#7c6df2,#9b7ff5)`, white glyph.

These track Tailwind defaults (gray/slate 900–50, blue-500/600, sky/violet/emerald/amber/pink) — prefer Tailwind classes over raw hex.

Typography: `system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`. Room name 12/750 uppercase; breadcrumb current 13.5/650; section/subsection rows 12.5/650; recording rows 12.5; snippets 10.5–11; counts/times tabular-nums.
Radius: search field & rows 8–9px; chips/pills 6–20px; room-icon tile 10px; dropdown 12px; avatar 50%.
Spacing: header padding 12×14; list padding 0×8 with 8–10px row padding; dropdown padding 8, item 9–10px.

## Assets
- **Icons** (inline SVG, 24×24, `fill:none; stroke:currentColor; stroke-width:2; round caps/joins`, lucide-style): search, folder, mic, chevron (down/right/rotated), back/arrow-left, globe, check, calendar, speakers (group), user (single), home. Reimplement as the repo's existing icon components — most already exist.
- **Room / user avatars**: existing brand/user avatar sources.
- No new raster assets required.

## Mapping to the existing codebase (`apps/web/src`)
- The left-panel container/list component (the current sections/subsections tree — likely under `components/` alongside the `List / Calendar / Actions / Tags` rail). Replace the always-expanded tree with the drill-in list + breadcrumb.
- Add a **room-switcher** header component (the header currently showing `PLATFORM ADMINISTRATOR` + collapse caret) wired to the rooms source and `currentRoomId`.
- Add/extend a **search** component with scope chip + `folder`/`everywhere` toggle + filter chips; back it with the existing search endpoint/query (extend for scope + snippet + breadcrumb if needed).
- Routing: section row → section page route; subsection drill → next level / subsection page route; recording row → existing recording-detail route (the hub from the previous handoff). Reuse existing route params.
- Keep i18n: all visible strings via `useTranslation` (namespaces `workspace`, `recordings`, `common`).
- Persist `currentRoomId` + last `drillPath` via the same store/localStorage pattern the panel uses today for the active tab.

## Files
- `Diariz Left Nav.html` — high-fidelity prototype (open in a browser). Contains all states in **light (5a)** and **dark (5b)**: idle drill-in, room switcher open, scoped search, everywhere search.
- `screenshots/nav-light.png` — light-mode reference render.
- `screenshots/nav-dark.png` — dark-mode reference render.
