# Handoff: Live "Notes while recording" panel — one unified stream

## Overview

Diariz's live notes panel (shown while a recording is running) currently splits **Notes** and
**Transcript** into two tabs, the live transcript carries no per-line timestamps, and there is no way to
push the live transcript or a screen capture into the chat prompt from the panel.

This handoff replaces that panel with **one stamped stream**: the user's notes, the screen captures and
the live transcript lines interleaved on a single timeline, with a composer permanently docked at the
bottom, a **Use in chat** action that sends the whole live transcript into the chat prompt as context, and
a per-capture **Chat** button plus drag-to-chat. Tabs are gone.

Design intent: the user is often presenting or talking while using this. Every primary action must be
reachable without reading — a fixed composer position, one visible button per action, global hotkeys, and
confirmations that appear where the user is already looking.

## About the design files

The files in this bundle are **design references created in HTML** (Design Components — a single
`.dc.html` file that renders in a browser). They are prototypes of the intended look and behaviour, **not
production code to copy**. The task is to recreate them inside Diariz's existing web app —
**React + TypeScript + Vite + Tailwind**, in `apps/web/src` — reusing its established patterns:

- the `--hub-*` CSS custom property layer in `apps/web/src/index.css` (light values on `:root`, dark
  overrides under `.dark`) — **use the tokens, not the literal hex values quoted below**;
- `components/hub/HubPopover.tsx` for the popover shell, `components/hub/HubIconButton.tsx` for icon
  buttons, `components/hub/CaptureControls.tsx` for the capture row;
- `react-i18next` for every string (`workspace` and `recordings` catalogues);
- the notes channel (`lib/notesChannel.ts`) so the detached window at `/notes-popout` and the inline
  popover stay identical, and `lib/useLiveTranscript.ts` for the live transcript state.

The prototype uses literal hex values only because it has no access to the app's stylesheet. Every value
listed under **Design tokens** below is quoted with the `--hub-*` token it came from.

## Fidelity

**High fidelity.** Colours, type sizes, weights, spacing, radii and interaction behaviour are final and
were derived from the existing components in `apps/web/src`. Recreate the layout as specified, but express
it through the app's tokens and shared components rather than new inline styles.

## Screens / views

### 1. Notes panel — inline popover (400px)

**Purpose.** Take a stamped note, capture the screen, read the delayed live transcript, and push either
the transcript or a capture into the chat prompt — without leaving the recording bar.

Screenshots: `screens/2a-one-stream-dark.png`, `screens/2a-one-stream-light.png`.

**Shell.** `HubPopover`, `width={400}`, `anchorClassName="right-0"`. Panel: `--hub-popover-bg`,
`1px solid --hub-popover-border`, `border-radius: 14px`, `box-shadow: --hub-popover-shadow`,
`animation: popIn .14s ease`. Column flex, no gap — the five bands below own their own padding.

**Band 1 — header** (`padding: 14px 14px 0`; row, `align-items: center`, `gap: 10px`)
- Recording dot: 9×9px circle, `--hub-red`, `animation: blink 1.2s infinite`.
- "Recording": 15px/700 system-ui, `--hub-text`.
- Elapsed clock: 13px/500 monospace, `--hub-muted`, `mm:ss`, ticking every second.
- Pop-out button (`margin-left: auto`) and Close button: 28×28px, `border-radius: 8px`, no border,
  transparent, `--hub-muted` icon; hover `background: --hub-surface-hover`, `color: --hub-text`. 16px
  stroke-2 glyphs — the existing `IconPopOut` / `IconClose` from `NotesPopover.tsx`, unchanged.

**Band 2 — action row** (`padding: 10px 0 8px`; row, `gap: 6px`)
- **Use in chat** (left, primary): height 30px, `padding: 0 11px`, `border-radius: 9px`,
  `background: --hub-blue-soft-bg`, `1px solid --hub-blue-soft-border`, `color: --hub-blue-text`,
  12px/600 label, 15px speech-bubble-with-arrow glyph, `gap: 7px`. Hover: `background:
  rgba(47,107,237,.28)` dark / `rgba(47,107,237,.18)` light (the `.hub-tags-pill` hover step).
  Tooltip: "Send everything transcribed so far into the chat prompt as context".
  On click it is **replaced in place** by the confirmation (see Interactions).
- Capture / auto-capture / capture-area (right, `margin-left: auto`, `gap: 6px`): three 30×30px
  `HubIconButton size="sm"` (the prototype uses 30px rather than 28px because they now sit on the
  primary row; keep 28px if you prefer the shared component untouched). Glyphs and disabled rules are
  exactly today's `CaptureControls` — camera, frame-stack with blinking dot, crop marks; auto-capture
  when running takes `pressed` styling (`border: 1px solid --hub-red`, `background:
  --hub-surface-hover`, `color: --hub-text`).

**Band 3 — filter chips** (`padding-bottom: 8px`, `border-bottom: 1px solid --hub-divider`; row, `gap: 4px`)
- Chips: `padding: 4px 9px`, `border-radius: 7px`, no border. Selected: `background:
  --hub-surface-hover`, `color: --hub-text`, 12px/600. Unselected: transparent, `--hub-muted`, 12px/400;
  hover `--hub-surface-hover` + `--hub-text-2`.
- Labels: "Everything", "Notes {n}", "Captures {n}" with live counts.
- Right end (`margin-left: auto`): 10px/500 `--hub-placeholder` state line — empty for Everything,
  "notes only" / "captures only" otherwise.

**Band 4 — the stream** (fixed `height: 300px`, `overflow-y: auto`, `padding: 10px 14px`, column,
`gap: 2px`). Fixed height, not `max-height`: the composer must never move under the user's hands as
lines arrive. Sorted by timestamp ascending; auto-scrolls to the tail while the user is at the tail.
Three row types share a 34px right-aligned stamp column (`flex-shrink: 0`, `width: 34px`,
`text-align: right`, 11px monospace):

- **Transcript line** — `padding: 3px 4px`, `border-radius: 7px`; hover `background:
  rgba(255,255,255,.04)` dark / `rgba(15,23,42,.04)` light. Stamp `--hub-muted` **(this is the
  timestamp-per-line the redesign adds — do not use `--hub-placeholder`, it fails AA at 11px)**. Text
  13px/1.65, `--hub-text-2`. Speaker name only when the speaker changes: 11px/600, `--hub-muted`,
  `margin-right: 6px`; a suggested identity keeps today's italic + trailing "?" treatment from
  `LiveTranscriptPanel.tsx`. Trailing **＋** button, 20×20px, `--hub-placeholder`, revealed on hover:
  "Write a note about this moment" — see Interactions.
- **Note line** — `padding: 5px 4px 5px 0`, `border-radius: 7px`, `background: rgba(47,107,237,.08)`
  dark / `rgba(47,107,237,.06)` light, `box-shadow: inset 2px 0 0 --hub-blue` (the left rail). Stamp
  11px/600 `--hub-blue-text`. Text 13px/1.6/500, `--hub-text`. Edit (pencil, 12px) and delete (✕, 12px)
  buttons, 20×20px, `--hub-muted`; delete hover `background: rgba(229,72,77,.16)`, `color:
  --hub-red-text`.
- **Capture row** — stamp `--hub-muted`, then a thumbnail: 150×84px, `border-radius: 8px`,
  `1px solid --hub-border` (dark: `rgba(255,255,255,.14)`), `background: --hub-surface`,
  `cursor: grab`, `draggable`. Top-left pill: "drag to chat", 9px/500, `background: rgba(6,11,22,.72)`,
  `border-radius: 5px`, `padding: 2px 5px`, with a 9px grip glyph. Bottom overlay bar (`padding: 5px`,
  `background: linear-gradient(transparent, rgba(6,11,22,.85))`, right-aligned, `gap: 4px`): **Chat**
  button — 22px tall, `padding: 0 7px`, `border-radius: 6px`, `background: rgba(47,107,237,.9)`,
  white 10px/600 label, 11px arrow glyph — and a 22×22px delete button, `background: rgba(6,11,22,.8)`,
  `color: --hub-red-text`.

**Band 5 — status + composer** (`padding: 8px 14px 14px`, `border-top: 1px solid --hub-divider`; column,
`gap: 7px`)
- Status line: 6×6px `--hub-green` dot, `animation: blink 1.6s infinite`, then 11px/400 `--hub-muted`:
  "Live · transcript 16s behind" (the existing `liveTranscriptBehind` / `liveTranscriptLive` /
  `liveTranscriptDegraded` strings). Capture-sent confirmation appears at the right end of this line,
  11px/600 `--hub-green-text`.
- Composer: row, `gap: 8px`, `background: --hub-surface`, `1px solid rgba(47,107,237,.45)`,
  `border-radius: 10px`, `padding: 7px 9px`. Left: live stamp badge, 11px/600 monospace,
  `--hub-blue-text` on `--hub-blue-soft-bg`, `border-radius: 5px`, `padding: 2px 5px`. Middle:
  borderless transparent input, 13px, `--hub-text`, placeholder "Note this moment…"
  (`--hub-placeholder`), autofocus on open. Right: a `⏎` key hint, 10px monospace,
  `--hub-muted-2`, `1px solid --hub-field-border`, `border-radius: 4px`, `padding: 1px 4px`.
- Hotkey line: 10px/400 `--hub-placeholder` — "Ctrl+Shift+N note anywhere · Ctrl+Shift+S capture ·
  Ctrl+Shift+C send transcript to chat" (⌘⇧ on macOS).

### 2. Notes panel — detached pop-out window (`/notes-popout`)

Screenshot: `screens/1a-popout-window-dark.png`.

Same five bands, same components, differences only:
- Window 420×740px minimum, `background: --hub-popover-bg`, full-height column, `overflow: hidden`.
- A 40px title bar: `background: --hub-bar-bg`, `border-bottom: 1px solid --hub-bar-border-bottom`,
  `padding: 8px 10px 8px 12px`; recording dot, "Diariz — live notes" 12px/600 `--hub-text-2`, elapsed
  clock 12px monospace `--hub-muted`; then an **On top** toggle (24px tall, `--hub-blue-soft-bg` /
  `--hub-blue-soft-border` / `--hub-blue-text`, 10px/600, 12px pin glyph), a **Compact** button
  (24×24px, `1px solid --hub-border`) that collapses the window to the composer band only, and Close.
- The stream takes `flex: 1` instead of a fixed 300px; composer input steps up to 14px.
- Capture thumbnails 170×96px.
- Offline behaviour is unchanged from today: when the channel to the host is lost, the composer is
  `disabled` (not hidden) and `CaptureControls` takes `unavailableReason`.

## Interactions & behaviour

| Trigger | Behaviour |
| :--- | :--- |
| Type + **Enter** in composer | Files a note at the composer's stamp; the note inserts into the stream in timestamp order, the input clears, focus stays. Empty/whitespace does nothing. |
| **＋** on a transcript line | Pins the composer stamp to that line's second and focuses the input. The stamp badge stops tracking the clock until the note is filed or the pin is cleared. Lets a note about something said 40s ago still be stamped there. |
| **Use in chat** | Sends the whole live transcript so far into the chat prompt as sticky context. The button is **replaced in place** by a confirmation pill — "Transcript sent to chat", 12px/600 `--hub-green-text` on `--hub-green-soft-bg` / `--hub-green-soft-border`, 14px tick — for ~2.6s, then reverts. The panel does **not** close and focus does **not** move to chat. |
| Capture **Chat** button | Adds that capture to the chat prompt (existing `SCREENSHOT_DRAG_TYPE` payload / `ChatScreenshotTray` behaviour). Confirms on the live status line: "Capture added to chat", ~2.4s. |
| Drag a capture thumbnail | `dataTransfer.setData(SCREENSHOT_DRAG_TYPE, {recordingId, screenshotId, capturedAtMs})`, `effectAllowed = "copy"` — the payload the chat composer already accepts. `cursor: grab`. |
| Camera button | Captures immediately and inserts a capture row at the current second. Disabled with `disabledReason` until a capture area is set (unchanged rule). |
| Auto-capture toggle | Unchanged; `pressed` styling and the blinking dot in the glyph mark it running. |
| Filter chips | Filter the stream to notes / captures only. Counts are live. |
| Hotkeys (desktop shell) | `Ctrl/⌘+Shift+N` focus composer, `Ctrl/⌘+Shift+S` capture, `Ctrl/⌘+Shift+C` transcript to chat — global, so they work while the call has focus. |
| Pop-out | Detaches into the always-on-top window; the inline popover collapses back when it closes (existing `useNotesPopout` + `notesChannel`). |
| Escape / backdrop click | Closes the popover (existing `HubPopover`). |

Animations are limited to what the app already defines in `index.css`: `popIn .14s ease` on the popover
and `blink 1.2s infinite` on recording indicators (`1.6s` on the green live dot). No other motion — a
panel that animates while someone is presenting is a distraction.

## State

Panel-local:
- `draft: string` — composer text.
- `pinnedAtMs: number | null` — the stamp taken over from a transcript line; `null` = follow the clock.
- `filter: "all" | "notes" | "captures"`.
- `transcriptSent: boolean`, `captureSent: boolean` — transient confirmations, cleared on a timer.

From the host, unchanged from today: `lines: MeetingNote[]` with `add/edit/delete`, `shots: PendingShot[]`
with `deleteShot`, `liveTranscript`, `liveLagSeconds`, `liveDegraded`, `captureAreaSet`, `autoCapture`.

The stream is a **derived** list, not stored state: merge `lines`, `shots` and
`liveTranscript.segments` into one array sorted by `capturedAtMs` / `startMs` ascending, tagged by kind.
Note that `liveTranscript` replaces wholesale on each append (see `useLiveTranscript.ts`), so the merge
must be recomputed rather than appended to.

New host wiring needed:
- send-live-transcript-to-chat (the transcript text so far, marked as provisional/unfinished, as sticky
  chat context — the same framing `useLiveTranscript` already applies for chat over a running meeting);
- global hotkey registration in the desktop shell for the three shortcuts.

## Design tokens

All from `apps/web/src/index.css`. Light value / dark value.

| Token | Light | Dark | Used for |
| :--- | :--- | :--- | :--- |
| `--hub-popover-bg` | `#ffffff` | `#0e1729` | panel + window background |
| `--hub-popover-border` | `rgba(15,23,42,.1)` | `rgba(255,255,255,.11)` | panel border |
| `--hub-popover-shadow` | `0 16px 40px rgba(15,23,42,.16)` | `0 24px 60px rgba(0,0,0,.6)` | panel shadow |
| `--hub-bar-bg` | `#ffffff` | `#0a1120` | pop-out title bar |
| `--hub-surface` | `#f1f5f9` | `#131d31` | composer + thumbnail background |
| `--hub-surface-hover` | `rgba(15,23,42,.05)` | `rgba(255,255,255,.06)` | hover / selected chip |
| `--hub-border` | `rgba(15,23,42,.1)` | `rgba(255,255,255,.1)` | icon-button + thumbnail border |
| `--hub-field-border` | `rgba(15,23,42,.14)` | `rgba(255,255,255,.14)` | ⏎ key hint border |
| `--hub-divider` | `rgba(15,23,42,.08)` | `rgba(255,255,255,.08)` | band dividers |
| `--hub-text` | `#0f172a` | `#eef2f8` | titles, note text, input text |
| `--hub-text-2` | `#334155` | `#c7d0e0` | transcript text, window title |
| `--hub-muted` | `#64748b` | `#7c8aa3` | **transcript stamps**, speaker, status, clock |
| `--hub-muted-2` | `#94a3b8` | `#6b7890` | ⏎ hint |
| `--hub-placeholder` | `#94a3b8` | `#5e6b82` | input placeholder, hotkey line, ＋ at rest |
| `--hub-blue` | `#2f6bed` | `#2f6bed` | note left rail |
| `--hub-blue-soft-bg` | `rgba(47,107,237,.1)` | `rgba(47,107,237,.14)` | Use in chat, stamp badge |
| `--hub-blue-soft-border` | `rgba(47,107,237,.3)` | `rgba(47,107,237,.3)` | Use in chat border |
| `--hub-blue-text` | `#1e40af` | `#cfe0ff` | Use in chat label, note stamps |
| `--hub-red` | `#e5484d` | `#e5484d` | recording dot, pressed auto-capture border |
| `--hub-red-text` | `#dc2626` | `#ff8b8f` | delete controls |
| `--hub-green` | `#16a34a` | `#22c55e` | live dot |
| `--hub-green-text` | `#15803d` | `#22c55e` | confirmation text |
| `--hub-green-soft-bg` | `rgba(34,197,94,.12)` | `rgba(34,197,94,.14)` | confirmation pill |
| `--hub-green-soft-border` | `rgba(34,197,94,.3)` | `rgba(34,197,94,.22)` | confirmation pill border |

**Type scale** (system-ui throughout; monospace = `ui-monospace, Menlo, monospace` for every timestamp):
17px/700 legacy panel title · 15px/700 "Recording" · 13px/400 transcript + note + input · 13px/500
clock · 12px/600 chips, buttons, window title · 11px/600 note stamps, speaker, section labels ·
11px/400 transcript stamps, status · 10px/500–600 hotkey line, thumbnail pills · line-height 1.65 on
transcript, 1.6 on notes.

**Spacing:** 14px panel gutters · bands separated by 1px dividers, not gaps · 8px between composer parts
· 6px between icon buttons · 4px between chips · 2px between stream rows · 34px stamp column.

**Radii:** 14px panel · 12px pop-out window · 10px composer · 9px primary/icon buttons on the action row
· 8px 28px icon buttons, thumbnails · 7px stream rows, chips · 6px thumbnail overlay buttons · 5px stamp
badges.

**Accessibility:** transcript stamps must be `--hub-muted` or brighter — `--hub-placeholder` measures
3.37:1 on the dark panel at 11px and fails AA. Every icon-only control keeps `aria-label` + `title`
(existing `HubIconButton` contract). Confirmations use `role="status"`; the lag line keeps
`aria-live="polite"` and is deliberately **not** `role="alert"`.

## Assets

None new. All glyphs are inline 24×24 `viewBox` SVGs, `fill="none"`, `stroke="currentColor"`,
`stroke-width="2"`, round caps/joins — the existing `Glyph` pattern from `CaptureControls.tsx`. Camera,
frame-stack and crop-mark glyphs are copied verbatim from that file; pop-out and close from
`NotesPopover.tsx`. New in this design: a speech-bubble-with-arrow (Use in chat), a right-arrow (capture
→ Chat), a pin (On top), and a 6-dot grip (drag hint) — all drawn in the same style, source in the
prototype files. Capture thumbnails are placeholders in the prototype; real ones come from
`api.screenshotThumbUrl(recordingId, shotId)` / the pop-out's `ShotView.thumb` blob.

## Files in this bundle

| File | What it is |
| :--- | :--- |
| `NotesPanel.dc.html` | Dark mode. Top section is the final design (`2a`), interactive: clock ticks, Enter files a note, camera adds a capture, chips filter, ＋ pins the stamp, Use in chat confirms. Below it, turn 1 holds the two explored directions (`1a` one stream — chosen; `1b` reading pane + docked deck) including the pop-out window layouts. |
| `NotesPanelLight.dc.html` | The final design in light-mode token values. |
| `NotesPanelCurrent.dc.html` | Pixel recreation of **today's** panel (Notes / Transcript tabs) for before-and-after. |
| `screens/2a-one-stream-dark.png` | Final panel, dark. |
| `screens/2a-one-stream-light.png` | Final panel, light. |
| `screens/1a-popout-window-dark.png` | Detached always-on-top window, dark. |

Source components this design replaces or touches: `apps/web/src/components/hub/NotesPopover.tsx`,
`apps/web/src/components/hub/LiveTranscriptPanel.tsx`, `apps/web/src/components/hub/CaptureControls.tsx`,
`apps/web/src/components/hub/ShotStrip.tsx`, `apps/web/src/components/NotesSection.tsx`,
`apps/web/src/pages/NotesPopout.tsx`, `apps/web/src/lib/notesChannel.ts`,
`apps/web/src/lib/useLiveTranscript.ts`, `apps/web/src/index.css`.
