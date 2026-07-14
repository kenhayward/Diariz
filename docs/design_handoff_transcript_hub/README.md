# Handoff: Diariz Recording-Detail Hub

## Overview
Redesign of the **recording-detail centre panel** in Diariz (`apps/web`). The current panel uses an 8-tab strip (Overview / Minutes / Actions / Notes / Speakers / Transcript / Attachments / Formulas) plus a crowded header toolbar; it scans poorly and breaks on low-res screens. This design replaces the tab strip with a **hub**: a hero summary card + a grid of capability tiles, each with a clear colored icon, a count/preview, and in-place actions. Drilling into a tile (e.g. Transcript) shows that section with a breadcrumb back to the hub.

## Scope (important)
This handoff covers **only the recording-detail centre panel** — the title/header, the hub, and the section (drill-in) views. The **global application top bar** shown in the screenshots (logo, mic-device select, System audio, record, upload, auto-stop, avatar) is **out of scope for this pass** — leave it exactly as it is today. It appears in the mocks purely for context; do not restyle or rebuild it. We will look at the top bar separately another time.

## About the Design Files
The file in this bundle (`Diariz Transcript Hub.dc.html`) is a **design reference created in HTML** — a prototype showing intended look and behavior, **not production code to copy directly**. The task is to **recreate this design in the existing `apps/web` codebase** using its established stack and patterns: **React + TypeScript + Vite + Tailwind CSS v4 (class-based dark mode) + react-i18next + @tanstack/react-query + react-router-dom**. Reuse existing components/hooks where they already exist (see “Mapping to the existing codebase” below); do not introduce a new styling system.

> Note on the HTML: it uses inline `style="…"` and CSS variables (`--bg`, `--accent`, …) purely so the prototype renders standalone. In the app, translate these to **Tailwind classes** matching the values in Design Tokens below. Icons are inline `<svg>` — reimplement as small React icon components (the repo already inlines lucide-style stroke icons, e.g. `DetailToolbar.tsx`, `ToolbarButton.tsx`).

## Fidelity
**High-fidelity.** Final colors, typography, spacing, iconography, and layout. Recreate pixel-accurately with Tailwind. All copy in the mock is placeholder sample data — bind to real recording data.

## Screens / Views

### 1. Hub (landing) — replaces the current `Overview` default
- **Purpose**: At-a-glance home for a recording; every capability discoverable in one view.
- **Layout**: Full-height column.
  - **Global app bar**: shown for context only — **out of scope, do not touch** (see Scope above).
  - **Detail header**: recording title (`h1`, 22px/650) on the left; action cluster on the right — a primary **Play** button (accent) + icon buttons **Copy link**, **Download**, **More** (kebab). This is the consolidated version of the old `DetailToolbar` (Rename / Copy link / Retranscribe / Move / Email / Download / kebab) — keep only Play + Copy link + Download visible; everything else lives in **More**. Padding `18px 28px`.
  - **Body**: vertical stack, `gap:14px`, padding `16px 28px`.
    1. **Hero summary card** (full width).
    2. **Tiles grid**: `display:grid; grid-template-columns:repeat(3,1fr); gap:12px; align-content:start`. Tiles are **content-height and top-aligned** (do NOT stretch them to fill — that was the excess-whitespace bug). On narrower widths, drop to 2 columns then 1.

#### Hero summary card
- Container: `background: linear-gradient(135deg, #182130, #1f2a3b)`; `border:1px solid #323d4e`; `border-radius:14px`; padding `18px 22px`.
- **Row 1**: green rounded-square icon tile (34×34, `border-radius:9px`, bg `rgba(52,211,153,.16)`, minutes/document glyph) + label **“Summary”** (15px/650) + **meeting-type chip** + right-aligned link **“Open full minutes →”** (12.5px, link color).
  - **Meeting-type chip is a DROPDOWN** to change the template: purple pill (`bg rgba(167,139,250,.16)`, `border 1px rgba(167,139,250,.3)`, text `#a78bfa`, `border-radius:20px`, 12px/600), template glyph + label + chevron-down. Selecting a template **changes the Minutes format and the available Formulas** — wire it to the same setting the backend uses for template selection.
- **Row 2 (detail chips)**: wrapping flex, `gap:8px`. Each chip: `bg #111827; border:1px solid #26303f; border-radius:8px; padding:4px 10px; font-size:12px`, colored leading icon. Chips: `📅 30 Jun 2026 · 19:26` (cyan), `🕐 21 min`, `🔊 Audio available · 16d left` (audio green, retention amber), `🌐 EN` (purple), overlapping speaker avatars + `4 speakers`.
- **Summary paragraph**: shown **inline** (13.5px/1.6, `#c9d1de`), ~1 paragraph. Do not gate behind hover/click. If a summary runs long, add a “Show more” expander rather than truncating silently.

#### Tiles (each: `bg #182130; border:1px solid #26303f; border-radius:14px; padding:16px 18px; display:flex; flex-direction:column`)
Header of every tile: 32×32 rounded-square icon tile (`border-radius:9px`, tinted bg per color) + title (14px/650) + subtitle (11.5px, muted). Then either a nav chevron (→) OR an action button.
- **Transcript** (blue): subtitle “142 segments · 21 min”; body = compact waveform bar (one bar accent-colored as playhead). Whole tile navigates to the Transcript view.
- **Actions** (amber): subtitle “5 open · 2 done”; body = 2 checklist preview rows (unchecked box + text). Navigates to Actions.
- **Speakers** (pink): subtitle “4 identified”; body = overlapping speaker avatars. Navigates to Speakers.
- **Notes** (purple): subtitle “2 notes”; header has **“+ New”** button (opens new-note editor); body = 2-line clamped preview of the latest note.
- **Files** (cyan): subtitle “3 attached”; header has **“+ Add”** button (opens the add-file / add-URL flow — existing `AttachmentsManager`); body = up to 3 file rows (icon + name + size; URL rows show link icon + link color).
- **Formulas** (blue): subtitle “1 run”; header has **“Run”** button (accent, opens `FormulaRunModal`); body = run rows (status dot + name + “Ready”), plus a muted “From <template>” line tying it to the meeting-type template.

Icon/action buttons are consistent pills: `bg #1f2a3b; border:1px solid #323d4e; border-radius:8px; padding:5px 10px; font-size:11.5px/600; color:#7cb0ff` for **+ New / + Add**; **Run** uses the accent fill (`bg #2563eb; color:#fff`).

### 2. Transcript (drilled-in) — with audio embedded in the flow
- **Purpose**: Read/scrub the transcript with audio inline.
- **Layout**: header with **breadcrumb** `‹ Overview  ›  Transcript` (back chip navigates to hub) and a right-aligned “142 segments” count.
- **Embedded audio + conversation-flow bar** (`bg #182130; border:1px solid #26303f; border-radius:12px; padding:13px 16px`):
  - Circular **play/pause** (40px, accent) + current time (`00:15`) + total (`21:00`).
  - **Conversation-flow track** instead of a plain waveform: a full-width horizontal bar segmented by speaker, each segment width proportional to that speaker’s talk-time, colored by speaker. A **playhead** (white line + dot) marks position; the track is the scrub target (click/drag to seek).
  - **Speaker legend** below: colored square + name + % of talk time.
  - **Show original / Show revised** toggle (maps to existing `transcriptView.ts` `toggleLabel`).
- **Segments list**: each row = 34px speaker avatar (initials on speaker color) + name + timestamp + text (14px/1.55). The **currently-playing segment is highlighted** (accent left border `3px`, tint `rgba(59,130,246,.1)`, a “▶ Playing” pill); already-played segments dim to `opacity:.6`.

## Interactions & Behavior
- **Tiles**: nav tiles (Transcript/Actions/Speakers) route to that section; action tiles (Notes/Files/Formulas) open their create/run modal from the header button but still allow navigating into the section (via title). Because a button can’t be nested in an `<a>`, implement tiles as a `div` with an inner navigate handler + separate action button (stop propagation), or a card link with an adjacent button.
- **Meeting-type chip**: opens a template dropdown; on change, refetch/regenerate Minutes and refresh the Formulas list for the new template.
- **Transcript**: play/pause toggles audio; clicking a segment seeks to its start; clicking/dragging the flow track seeks; the playing segment auto-scrolls into view; show-original toggles between `seg.original` and `seg.text`.
- **Navigation**: persist the active section (the old page persisted the active tab key) — keep that behavior via the same store/localStorage key.
- **Responsive**: tiles grid 3 → 2 → 1 columns as width shrinks; header action cluster collapses extra buttons into **More** earlier on narrow widths; hero chips wrap.

## State Management
- `activeSection: 'hub' | 'transcript' | 'minutes' | 'actions' | 'notes' | 'speakers' | 'files' | 'formulas'` (persisted).
- `recording` (existing react-query `["recordings", id]` / detail query).
- Derived counts for badges: `segments.length`, actions open/done, speakers count, notes count, attachments count, formula runs count.
- Audio player: `isPlaying`, `currentTime`, `duration`, `showOriginal`.
- Template selection: current `templateId` (drives Minutes + Formulas).
- Modal flags: `addFileOpen`, `newNoteOpen`, `runFormulaOpen`.

## Design Tokens
Colors (hex):
- Panel bg `#111827`; deep bg `#0b1018`; surface `#182130`; surface-2 `#1f2a3b`; line `#26303f`; line-2 `#323d4e`.
- Text `#e9edf4`; body text `#d7dce6` / `#c9d1de`; muted `#9aa5b6`; faint `#6a7688`.
- Accent (primary) `#3b82f6`, accent-strong `#2563eb`, accent-soft `#16305a`; link `#7cb0ff`.
- Status/section: green `#34d399`, amber `#fbbf24`, pink `#f472b6`, purple `#a78bfa`, cyan `#38bdf8`.
- Speaker colors: KH `#60a5fa`, MD `#f472b6`, AL `#34d399`, PA `#fbbf24` (avatar text `#0b1018`).

These closely track Tailwind defaults (gray-900/800/700, blue-500/600, emerald-400, amber-400, pink-400, violet-400, sky-400) — prefer the equivalent Tailwind classes over raw hex.

Typography: `system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`. Title 22–24/650; card title 14/650; section label 15/650; body 13.5–14/1.55–1.6; chips/subtitles 11.5–12.5; tabular-nums for times.
Radius: chips 8px & pills 20px; icon tiles 9px; cards 14px; player 12px.
Spacing: card padding 16–22px; body padding 16–28px; grid gap 12px; chip gap 8px.
Shadow (floating panel only, not needed in-app): `0 14px 46px rgba(0,0,0,.4)`.

## Assets
- **Icons**: inline SVG (24×24, `fill:none; stroke:currentColor; stroke-width:2; round caps/joins`), lucide-style. Each card icon is **duotone** — outline in the section color + a small filled accent (e.g. Transcript = speech bubble + amber spark; Actions = clipboard + green check; Speakers = person + cyan sound waves; Notes = note + amber folded corner; Files = document + purple corner; **Formulas = the existing `images/formula-icon.svg`** flask + sparkles). Reimplement as React components; the Formulas glyph already exists in the repo.
- **Logo / avatar**: existing brand assets (`/logo.png`, user avatar) — use the codebase’s current sources.
- No new raster assets required.

## Mapping to the existing codebase (`apps/web/src`)
- Replace/augment `pages/RecordingDetail.tsx` (currently renders `DetailTabs` + per-tab toolbars).
- `components/DetailTabs.tsx` → the new hub + section router (the “tabs” become tiles + drill-in).
- `components/DetailToolbar.tsx` → consolidated header cluster (Play + Copy link + Download + More).
- Reuse tab bodies: `ActionsTab.tsx`, `AttachmentsManager.tsx`, `FormulasPanel.tsx` / `FormulaRunModal.tsx`, speakers view, transcript view + `lib/transcriptView.ts`, `lib/actionsView.ts`.
- Counts: derive from the same DTOs used today (`lib/types.ts`).
- Keep i18n: all visible strings via `useTranslation` (namespaces `workspace`, `recordings`, `common`).

## Files
- `Diariz Transcript Hub.dc.html` — the high-fidelity prototype (open in a browser). Contains both the **Hub (landing)** and **Transcript (drilled-in)** frames at 1280×720.
- `screenshots/hub-landing.png` — reference render of the hub. *(The top bar visible here is out of scope — see Scope.)*
- `screenshots/transcript-view.png` — reference render of the drilled-in Transcript view with the embedded audio + conversation-flow track. *(Top bar out of scope.)*
